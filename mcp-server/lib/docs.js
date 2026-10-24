// Document text for the vault: DOCX and XLSX read with Node's own zlib (no dependency), PDF through unpdf (pdf.js,
// loaded only when a PDF is read; needs Node 22+). Every reader returns { kind, units: [...] } where a unit is one page
// (PDF), one paragraph (DOCX) or one row (XLSX), each with its location, so a requirement's quote can be found again
// at the page, paragraph or cell it cites.
import fs from "node:fs";
import zlib from "node:zlib";
import crypto from "node:crypto";
import path from "node:path";

export const sha256 = (buf) => crypto.createHash("sha256").update(buf).digest("hex");

// Normalised text for hashing and quote matching (method 02, section 3.2): NFKC, one kind of quote and dash, no soft
// hyphens, words split across lines joined, whitespace collapsed, lower case.
export function normalise(s) {
  return String(s ?? "").normalize("NFKC")
    .replace(/­/g, "")
    .replace(/[‘’‚‛′]/g, "'").replace(/[“”„‟″]/g, '"')
    .replace(/[‐-―−]/g, "-")
    .replace(/(\w)-\s*\n\s*(\w)/g, "$1$2")
    .replace(/…/g, "...")
    .replace(/\s+/g, " ").trim().toLowerCase();
}

// ---- zip (DOCX / XLSX are zip files) ------------------------------------------------------------------------------

export function unzip(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65557); i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new Error("not a zip file");
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const files = new Map();
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error("damaged zip directory");
    const method = buf.readUInt16LE(p + 10), size = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28), extraLen = buf.readUInt16LE(p + 30), commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString("utf8", p + 46, p + 46 + nameLen);
    files.set(name, () => {
      const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
      const data = buf.subarray(start, start + size);
      return method === 0 ? data : zlib.inflateRawSync(data);
    });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return { names: [...files.keys()], read: (name) => files.has(name) ? files.get(name)().toString("utf8") : null };
}

export const decode = (s) => s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&apos;/g, "'")
  .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(+d)).replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCodePoint(parseInt(h, 16))).replace(/&amp;/g, "&");
export const attr = (tag, name) => { const m = new RegExp(`\\b${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`).exec(tag); return m ? decode(m[1] ?? m[2]) : null; };

// ---- DOCX: paragraphs with their heading path; table cells joined by " | " -----------------------------------------

export function readDocx(buf) {
  const zip = unzip(buf);
  const xml = zip.read("word/document.xml");
  if (!xml) throw new Error("no word/document.xml");
  const units = [], headings = [];
  let tracked = /<w:(ins|del)\b/.test(xml), index = 0;
  const body = xml.replace(/<w:del\b[\s\S]*?<\/w:del>/g, "");   // deleted text (unaccepted changes) is not the document's text
  // Tables become one unit per row; paragraphs outside tables one unit each.
  const parts = body.split(/(<w:tbl>[\s\S]*?<\/w:tbl>)/);
  const paraText = (p) => decode([...p.matchAll(/<w:t(?:\s[^>]*)?>([^<]*)<\/w:t>|<w:tab\/>|<w:br\/>/g)].map((m) => m[1] ?? " ").join(""));
  for (const part of parts) {
    if (part.startsWith("<w:tbl>")) {
      for (const row of part.match(/<w:tr\b[\s\S]*?<\/w:tr>/g) ?? []) {
        const cells = (row.match(/<w:tc\b[\s\S]*?<\/w:tc>/g) ?? []).map((c) => (c.match(/<w:p\b[\s\S]*?<\/w:p>/g) ?? []).map(paraText).join(" ").trim());
        if (cells.some(Boolean)) units.push({ paragraph: ++index, section: [...headings], table: true, text: cells.join(" | ") });
      }
      continue;
    }
    for (const p of part.match(/<w:p\b[\s\S]*?<\/w:p>/g) ?? []) {
      const text = paraText(p).trim();
      const style = attr(/<w:pStyle\b[^>]*>/.exec(p)?.[0] ?? "", "w:val") ?? "";
      const level = /^heading\s*(\d)$/i.exec(style.replace(/([a-z])(\d)$/i, "$1 $2"))?.[1];
      index++;
      if (!text) continue;
      if (level) { headings.length = Math.min(headings.length, +level - 1); headings[+level - 1] = text; }
      units.push({ paragraph: index, section: level ? headings.slice(0, +level - 1) : [...headings], heading: level ? +level : undefined, text });
    }
  }
  return { kind: "docx", trackedChanges: tracked, units };
}

// ---- XLSX: one unit per non-empty row, cells by column letter; hidden sheets flagged --------------------------------

export function readXlsx(buf, { sheets: only } = {}) {
  const zip = unzip(buf);
  const shared = [];
  const ss = zip.read("xl/sharedStrings.xml");
  if (ss) for (const si of ss.match(/<si>[\s\S]*?<\/si>/g) ?? []) shared.push(decode([...si.matchAll(/<t(?:\s[^>]*)?>([^<]*)<\/t>/g)].map((m) => m[1]).join("")));
  const wb = zip.read("xl/workbook.xml") ?? "";
  const rels = zip.read("xl/_rels/workbook.xml.rels") ?? "";
  const target = {};
  for (const r of rels.match(/<Relationship\b[^>]*>/g) ?? []) target[attr(r, "Id")] = attr(r, "Target");
  const units = [], sheetList = [];
  for (const s of wb.match(/<sheet\b[^>]*>/g) ?? []) {
    const name = attr(s, "name"), hidden = /state="(hidden|veryHidden)"/.test(s);
    sheetList.push({ name, hidden });
    if (only && !only.includes(name)) continue;
    let file = target[attr(s, "r:id")] ?? "";
    file = file.startsWith("/") ? file.slice(1) : path.posix.join("xl", file);
    const xml = zip.read(file);
    if (!xml) continue;
    for (const row of xml.match(/<row\b[\s\S]*?<\/row>/g) ?? []) {
      const r = +attr(row.slice(0, row.indexOf(">") + 1), "r");
      const cells = {};
      for (const c of row.match(/<c\b[^>]*\/>|<c\b[\s\S]*?<\/c>/g) ?? []) {
        const open = c.slice(0, c.indexOf(">") + 1), ref = attr(open, "r") ?? "", t = attr(open, "t");
        const v = /<v>([\s\S]*?)<\/v>/.exec(c)?.[1], inline = /<is>[\s\S]*?<t[^>]*>([\s\S]*?)<\/t>/.exec(c)?.[1];
        const value = t === "s" ? shared[+v] : t === "inlineStr" ? decode(inline ?? "") : v != null ? decode(v) : null;
        if (value != null && value !== "") cells[ref.replace(/\d+$/, "")] = value;
      }
      if (Object.keys(cells).length) units.push({ sheet: name, row: r, hiddenSheet: hidden || undefined, cells, text: Object.values(cells).join(" | ") });
    }
  }
  return { kind: "xlsx", sheets: sheetList, units };
}

// ---- PDF: one unit per page; pages with almost no text are flagged (scanned or image-only) -------------------------

export async function readPdf(buf) {
  let unpdf;
  try { unpdf = await import("unpdf"); }
  catch (err) { throw new Error(`Reading PDFs needs Node.js 22 or later (this is ${process.version}): ${err.message}`); }
  const pdf = await unpdf.getDocumentProxy(new Uint8Array(buf));
  const { text } = await unpdf.extractText(pdf, { mergePages: false });
  const units = text.map((t, i) => ({ page: i + 1, text: t, lowText: t.replace(/\s/g, "").length < 25 || undefined }));
  // Comments made as PDF annotations (client review comments) are not in the text layer: read them too.
  for (let i = 1; i <= pdf.numPages; i++) {
    const notes = (await (await pdf.getPage(i)).getAnnotations()).map((a) => a.contentsObj?.str || a.contents || "").filter(Boolean);
    if (notes.length) units.push({ page: i, annotation: true, text: notes.join("\n") });
  }
  return { kind: "pdf", pages: pdf.numPages, units };
}

export async function readDocument(file, opts = {}) {
  const buf = fs.readFileSync(file);
  const ext = path.extname(file).toLowerCase();
  if (ext === ".pdf") return readPdf(buf);
  if (ext === ".docx" || ext === ".docm") return readDocx(buf);
  if (ext === ".xlsx" || ext === ".xlsm") return readXlsx(buf, opts);
  if ([".txt", ".md", ".csv", ".json", ".xml", ".ifc"].includes(ext))
    return { kind: "text", units: textOf(buf).split(/\r?\n/).map((text, i) => ({ line: i + 1, text })).filter((u) => u.text.trim()) };
  throw new Error(`ACE cannot read ${ext || "this"} files (PDF, DOCX, XLSX and text files only).`);
}

// Text files in UTF-8 or UTF-16 (Revit's shared parameter files are UTF-16 LE with a byte order mark).
export function textOf(buf) {
  if (buf[0] === 0xff && buf[1] === 0xfe) return buf.subarray(2).toString("utf16le");
  if (buf[0] === 0xfe && buf[1] === 0xff) { const b = Buffer.from(buf.subarray(2)); b.swap16(); return b.toString("utf16le"); }
  return buf.toString("utf8").replace(/^\uFEFF/, "");
}

// Where a unit is, in words a reader can follow.
export const where = (u) => u.page != null ? `page ${u.page}${u.annotation ? " (comment)" : ""}` : u.sheet != null ? `sheet '${u.sheet}' row ${u.row}` : u.paragraph != null ? `paragraph ${u.paragraph}` : `line ${u.line}`;
