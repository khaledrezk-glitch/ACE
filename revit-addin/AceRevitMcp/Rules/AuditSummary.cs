using System;
using System.Text.Json.Nodes;
using AceRevitMcp.Commands;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Rules
{
    /// <summary>
    /// Bridge "audit_summary" (backlog V4): the last audit's summary kept inside the model, on Project Information, in
    /// extensible storage, so the result travels with the RVT even without its vault: date, report revision, score, open
    /// findings by severity and where the report is. The report itself stays in the vault (a few hundred bytes here,
    /// never the report). { } reads it; { summary: {...} } writes it like every change (check_only / dry_run / apply,
    /// ONE undo step). Anyone can read the entity; only ACE (vendor ACE) can write it.
    /// </summary>
    internal static class AuditSummary
    {
        public const string Name = "ACE: audit summary";
        private static readonly Guid SchemaId = new Guid("6F1B8E2A-3C4D-4E5F-9A0B-ACE0000A0D17");
        private const int MaxBytes = 16 * 1024;

        public static JsonNode Run(UIApplication app, JsonObject args)
        {
            var doc = Args.RequireDoc(app);
            if (args["summary"] is not JsonObject summary)
                return new JsonObject { ["model"] = doc.Title, ["summary"] = Read(doc), ["note"] = Read(doc) == null ? "No audit summary is stored in this model yet." : null };
            var text = summary.ToJsonString();
            if (text.Length > MaxBytes) throw new Bridge.CommandException($"The summary is {text.Length} characters; at most {MaxBytes} are kept in the model (keep details in the report).");
            return NativeChange.Run(app, args, "audit_summary", Name, (d, plan) =>
            {
                var info = d.ProjectInformation ?? throw new Bridge.CommandException("This document has no Project Information (a family?): the summary is kept in project models only.");
                if (d.IsWorkshared && WorksharingUtils.GetCheckoutStatus(d, info.Id) == CheckoutStatus.OwnedByOtherUser)
                    throw new Bridge.CommandException("Project Information is borrowed by another user: ask them to relinquish it, then try again.");
                var before = Read(d);
                if (!plan)
                {
                    var entity = new Entity(Schema());
                    entity.Set("Json", text);
                    entity.Set("Version", 1);
                    info.SetEntity(entity);
                }
                return new JsonObject { ["model"] = d.Title, ["element"] = "Project Information", ["before"] = before, ["after"] = JsonNode.Parse(text) };
            });
        }

        /// <summary>The stored summary, or null.</summary>
        internal static JsonNode Read(Document doc)
        {
            var schema = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);
            var info = doc.ProjectInformation;
            if (schema == null || info == null) return null;
            var entity = info.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return null;
            try { return JsonNode.Parse(entity.Get<string>("Json")); } catch { return null; }
        }

        private static Schema Schema()
        {
            var existing = Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);
            if (existing != null) return existing;
            var b = new SchemaBuilder(SchemaId);
            b.SetSchemaName("ACEAuditSummary");
            b.SetDocumentation("ACE: summary of the last model audit (the report is in the project's ACE Vault).");
            b.SetVendorId("ACE");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.AddSimpleField("Json", typeof(string));
            b.AddSimpleField("Version", typeof(int));
            return b.Finish();
        }
    }
}
