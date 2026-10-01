namespace AceRevitMcp.Util
{
    /// <summary>Revit stores lengths in feet; ACE talks in millimetres. One place for the conversion.</summary>
    internal static class Lengths
    {
        public const double MmPerFoot = 304.8;
        /// <summary>Millimetres to Revit's internal feet.</summary>
        public static double Ft(double mm) => mm / MmPerFoot;
        /// <summary>Revit's internal feet to millimetres.</summary>
        public static double Mm(double feet) => feet * MmPerFoot;
    }
}
