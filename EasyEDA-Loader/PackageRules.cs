using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Default package-size rules for passive components the user ticks as part of a BOM.
    /// Default footprints are 0402; anything marked as "Power" (e.g. decoupling/bulk caps
    /// on power rails, current-limiting/high-current resistors) bumps up to 0603.
    /// </summary>
    public static class PackageRules
    {
        public const string DefaultPackage = "0402";
        public const string PowerPackage = "0603";

        /// <summary>
        /// Resolve the package to use for a row given its designator, value/comment,
        /// and whether the user ticked the Power box.
        /// </summary>
        public static string ResolvePackage(string designator, string comment, bool isPower)
        {
            if (isPower)
                return PowerPackage;

            if (string.IsNullOrWhiteSpace(designator))
                return DefaultPackage;

            string prefix = DesignatorPrefix(designator);
            switch (prefix)
            {
                case "R":
                case "C":
                case "L":
                    return DefaultPackage;
                default:
                    return null;
            }
        }

        public static bool IsPassive(string designator)
        {
            string prefix = DesignatorPrefix(designator);
            return prefix == "R" || prefix == "C" || prefix == "L";
        }

        private static string DesignatorPrefix(string designator)
        {
            if (string.IsNullOrWhiteSpace(designator))
                return string.Empty;

            int i = 0;
            while (i < designator.Length && char.IsLetter(designator[i]))
                i++;

            return designator.Substring(0, i).ToUpperInvariant();
        }

        /// <summary>
        /// Build the most useful JLCPCB search keyword for a row, appending the package
        /// only for passives (where the package matters for an exact match).
        /// </summary>
        public static string BuildSearchKeyword(string designator, string comment, string package)
        {
            string value = string.IsNullOrWhiteSpace(comment) ? string.Empty : comment.Trim();
            if (IsPassive(designator) && !string.IsNullOrWhiteSpace(package))
                return string.IsNullOrWhiteSpace(value) ? package : $"{value} {package}";
            return value;
        }
    }
}
