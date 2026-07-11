using System.ComponentModel;

namespace EasyEDA_Loader
{
    /// <summary>
    /// One row in the BOM Builder tick list.
    /// Loaded from a schematic PDF (OCR), a BOM CSV/XLSX, or the live schematic.
    /// Resolved against JLCPCB by the BOM Builder when the user ticks it.
    /// </summary>
    public class BomRow : INotifyPropertyChanged
    {
        private bool _include = true;
        private bool _isPower;
        private string _package;
        private string _designator;
        private string _originalValue;
        private string _originalLcsc;
        private string _resolvedLcsc;
        private string _resolvedLibraryType;
        private int _resolvedStock;
        private double _resolvedUnitPrice;
        private string _resolvedDescription;
        private string _resolvedDatasheet;
        private string _resolutionNote;
        private bool _resolutionFailed;

        public string Sheet { get; set; }
        public int Quantity { get; set; } = 1;

        public string Designator
        {
            get => _designator;
            set { if (_designator != value) { _designator = value; OnPropertyChanged(nameof(Designator)); OnPropertyChanged(nameof(Package)); } }
        }

        public string OriginalValue
        {
            get => _originalValue;
            set { if (_originalValue != value) { _originalValue = value; OnPropertyChanged(nameof(OriginalValue)); OnPropertyChanged(nameof(Package)); } }
        }

        public string OriginalLcsc
        {
            get => _originalLcsc;
            set { if (_originalLcsc != value) { _originalLcsc = value; OnPropertyChanged(nameof(OriginalLcsc)); } }
        }

        public bool Include
        {
            get => _include;
            set { if (_include != value) { _include = value; OnPropertyChanged(nameof(Include)); } }
        }

        public bool IsPower
        {
            get => _isPower;
            set
            {
                if (_isPower != value)
                {
                    _isPower = value;
                    OnPropertyChanged(nameof(IsPower));
                    OnPropertyChanged(nameof(Package));
                }
            }
        }

        public string Package
        {
            get => _package ?? PackageRules.ResolvePackage(_designator, _originalValue, _isPower);
            set { if (_package != value) { _package = value; OnPropertyChanged(nameof(Package)); } }
        }

        public string ResolvedLcsc
        {
            get => _resolvedLcsc;
            set { if (_resolvedLcsc != value) { _resolvedLcsc = value; OnPropertyChanged(nameof(ResolvedLcsc)); } }
        }

        public string ResolvedLibraryType
        {
            get => _resolvedLibraryType;
            set { if (_resolvedLibraryType != value) { _resolvedLibraryType = value; OnPropertyChanged(nameof(ResolvedLibraryType)); OnPropertyChanged(nameof(IsBasic)); } }
        }

        public bool IsBasic => string.Equals(_resolvedLibraryType, "base", System.StringComparison.OrdinalIgnoreCase);

        public int ResolvedStock
        {
            get => _resolvedStock;
            set { if (_resolvedStock != value) { _resolvedStock = value; OnPropertyChanged(nameof(ResolvedStock)); } }
        }

        public double ResolvedUnitPrice
        {
            get => _resolvedUnitPrice;
            set { if (_resolvedUnitPrice != value) { _resolvedUnitPrice = value; OnPropertyChanged(nameof(ResolvedUnitPrice)); } }
        }

        public string ResolvedDescription
        {
            get => _resolvedDescription;
            set { if (_resolvedDescription != value) { _resolvedDescription = value; OnPropertyChanged(nameof(ResolvedDescription)); } }
        }

        public string ResolvedDatasheet
        {
            get => _resolvedDatasheet;
            set { if (_resolvedDatasheet != value) { _resolvedDatasheet = value; OnPropertyChanged(nameof(ResolvedDatasheet)); } }
        }

        public string ResolutionNote
        {
            get => _resolutionNote;
            set { if (_resolutionNote != value) { _resolutionNote = value; OnPropertyChanged(nameof(ResolutionNote)); } }
        }

        public bool ResolutionFailed
        {
            get => _resolutionFailed;
            set { if (_resolutionFailed != value) { _resolutionFailed = value; OnPropertyChanged(nameof(ResolutionFailed)); } }
        }

        public string ResolvedPackage { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
