using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    /// <summary>
    /// Orchestrates the BOM Builder flow:
    ///   - holds the tick-list of BomRow items,
    ///   - resolves each ticked row against JLCPCB (preferring Basic parts and the requested package),
    ///   - exposes the resolved rows so the dialog can stamp them onto Altium components
    ///     and write the JLCPCB-ready CSV.
    /// </summary>
    public class BomBuilderViewModel
    {
        private readonly JlcpcbPartsApi _jlcpcbApi;

        public ObservableCollection<BomRow> Rows { get; } = new ObservableCollection<BomRow>();

        public List<string> Warnings { get; } = new List<string>();

        public string SourceDescription { get; set; }

        public BomBuilderViewModel(JlcpcbPartsApi jlcpcbApi = null)
        {
            _jlcpcbApi = jlcpcbApi ?? new JlcpcbPartsApi();
        }

        public void LoadRows(IEnumerable<BomRow> rows, string sourceDescription)
        {
            Rows.Clear();
            Warnings.Clear();
            SourceDescription = sourceDescription;
            foreach (var r in rows.OrderBy(r => r.Designator, StringComparer.OrdinalIgnoreCase))
                Rows.Add(r);
        }

        /// <summary>
        /// Append a blank row the user can fill in by hand (e.g. paste an LCSC C...
        /// number into the LCSC column). Returns the new row so the caller can scroll
        /// the grid to it / start editing.
        /// </summary>
        public BomRow AddBlankRow(string designator = null, string value = null, string lcsc = null)
        {
            var row = new BomRow
            {
                Designator = designator ?? "",
                OriginalValue = value ?? "",
                ResolvedLcsc = lcsc ?? "",
                OriginalLcsc = lcsc ?? "",
                Include = true,
            };
            Rows.Add(row);
            return row;
        }

        /// <summary>Remove a single row from the tick list (manual deletion).</summary>
        public bool DeleteRow(BomRow row)
        {
            if (row == null) return false;
            return Rows.Remove(row);
        }

        /// <summary>
        /// Resolve one row by its LCSC number directly (no keyword search). Use this
        /// when the user has typed an LCSC part number by hand -- it queries JLCPCB
        /// for that exact componentCode and fills in Basic/Extended, package, stock,
        /// price and datasheet. Returns true on success.
        /// </summary>
        public async Task<bool> ResolveByLcscAsync(BomRow row, CancellationToken cancellationToken)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.ResolvedLcsc)) return false;
            row.ResolutionFailed = false;
            row.ResolutionNote = null;

            try
            {
                var part = await _jlcpcbApi.LookupByLcscAsync(row.ResolvedLcsc, cancellationToken);
                if (part == null)
                {
                    row.ResolutionFailed = true;
                    row.ResolutionNote = "LCSC number not found on JLCPCB.";
                    return false;
                }
                row.OriginalLcsc = part.Lcsc;
                row.ResolvedLcsc = part.Lcsc;
                row.ResolvedLibraryType = part.LibraryType;
                row.ResolvedStock = part.Stock;
                row.ResolvedUnitPrice = part.UnitPrice;
                row.ResolvedDescription = part.Description;
                row.ResolvedDatasheet = part.DatasheetUrl;
                row.ResolvedPackage = part.Package;
                row.ResolutionNote = part.IsBasic ? "Basic" : "Extended";
                return true;
            }
            catch (Exception ex)
            {
                row.ResolutionFailed = true;
                row.ResolutionNote = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Resolve all ticked rows against JLCPCB. Each row is resolved independently so
        /// one failure doesn't stop the rest. Returns the count of successfully resolved rows.
        /// </summary>
        public async Task<int> ResolveAllAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            int ok = 0;
            var ticked = Rows.Where(r => r.Include).ToList();
            for (int i = 0; i < ticked.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var row = ticked[i];
                progress?.Report($"[{i + 1}/{ticked.Count}] {row.Designator} ...");
                try
                {
                    await ResolveRowAsync(row, cancellationToken);
                    if (!row.ResolutionFailed)
                        ok++;
                }
                catch (Exception ex)
                {
                    row.ResolutionFailed = true;
                    row.ResolutionNote = ex.Message;
                }
            }
            return ok;
        }

        public async Task ResolveRowAsync(BomRow row, CancellationToken cancellationToken)
        {
            row.ResolutionFailed = false;
            row.ResolutionNote = null;

            string keyword = PackageRules.BuildSearchKeyword(row.Designator, row.OriginalValue, row.Package);
            var part = await _jlcpcbApi.ResolveAsync(
                row.OriginalLcsc,
                keyword,
                row.Package,
                cancellationToken);

            if (part == null)
            {
                row.ResolutionFailed = true;
                row.ResolutionNote = "No JLCPCB match found. Edit the row and try again.";
                return;
            }

            row.ResolvedLcsc = part.Lcsc;
            row.ResolvedLibraryType = part.LibraryType;
            row.ResolvedStock = part.Stock;
            row.ResolvedUnitPrice = part.UnitPrice;
            row.ResolvedDescription = part.Description;
            row.ResolvedDatasheet = part.DatasheetUrl;
            row.ResolvedPackage = part.Package;
            row.ResolutionNote = part.IsBasic ? "Basic" : "Extended";
        }

        public IEnumerable<BomRow> ReadyToOrder =>
            Rows.Where(r => r.Include && !r.ResolutionFailed && !string.IsNullOrWhiteSpace(r.ResolvedLcsc));
    }
}
