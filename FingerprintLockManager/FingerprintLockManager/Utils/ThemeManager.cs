using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace FingerprintLockManager
{
    public enum AppTheme
    {
        Light,
        Dark
    }

    public static class ThemeManager
    {
        private static readonly IReadOnlyDictionary<string, string> LightPalette =
            new Dictionary<string, string>
            {
                ["PrimaryBrush"] = "#0F766E",
                ["PrimaryDarkBrush"] = "#115E59",
                ["PrimaryLightBrush"] = "#DDF7F2",
                ["PrimaryHoverBrush"] = "#0D9488",
                ["PrimaryForegroundBrush"] = "#FFFFFF",
                ["AccentBrush"] = "#D97706",
                ["BackgroundBrush"] = "#F5F7FA",
                ["CardBrush"] = "#FFFFFF",
                ["InputBrush"] = "#EEF2F6",
                ["TextBrush"] = "#172033",
                ["SubTextBrush"] = "#667085",
                ["BorderBrush"] = "#DDE3EA",
                ["SurfaceAltBrush"] = "#F8FAFC",
                ["SurfaceHoverBrush"] = "#F1F5F9",
                ["SurfacePressedBrush"] = "#E8EEF3",
                ["ReadOnlyBrush"] = "#F1F4F7",
                ["DangerBrush"] = "#DC2626",
                ["DangerSurfaceBrush"] = "#FEF2F2",
                ["DangerPressedBrush"] = "#FEE2E2",
                ["DangerBorderBrush"] = "#FECACA",
                ["SuccessBrush"] = "#15803D",
                ["WarningBrush"] = "#B45309",
                ["GridHeaderBrush"] = "#F0F3F7",
                ["GridHeaderTextBrush"] = "#475467",
                ["GridAlternateBrush"] = "#FAFBFC",
                ["GridLineBrush"] = "#E7ECF1",
                ["RowHoverBrush"] = "#EDF8F6",
                ["StatusBarBrush"] = "#EFF3F6",
                ["HintBrush"] = "#ECFDF5",
                ["HintBorderBrush"] = "#A7F3D0",
                ["SidebarBrush"] = "#111827",
                ["SidebarHoverBrush"] = "#1F2937",
                ["SidebarActiveBrush"] = "#153C3A",
                ["SidebarBorderBrush"] = "#273449",
                ["SidebarTextBrush"] = "#E5E7EB",
                ["SidebarMutedBrush"] = "#8F9BAB",
                ["SidebarIconBrush"] = "#B8C1CD",
                ["SidebarIconHoverBrush"] = "#243142",
                ["SidebarIconPressedBrush"] = "#2E3D50",
                ["SidebarAvatarBrush"] = "#273449",
                ["SidebarBrandBrush"] = "#0F766E",
                ["ConsoleBrush"] = "#0B1118",
                ["ConsoleAltBrush"] = "#0D141C",
                ["ConsoleHeaderBrush"] = "#111A24",
                ["ConsoleTextBrush"] = "#DDE5EE",
                ["ConsoleMutedBrush"] = "#8795A5",
                ["ShadowColor"] = "#0F172A"
            };

        private static readonly IReadOnlyDictionary<string, string> DarkPalette =
            new Dictionary<string, string>
            {
                ["PrimaryBrush"] = "#2DD4BF",
                ["PrimaryDarkBrush"] = "#14B8A6",
                ["PrimaryLightBrush"] = "#153A38",
                ["PrimaryHoverBrush"] = "#5EEAD4",
                ["PrimaryForegroundBrush"] = "#062923",
                ["AccentBrush"] = "#F59E0B",
                ["BackgroundBrush"] = "#0B0F14",
                ["CardBrush"] = "#111820",
                ["InputBrush"] = "#0B1118",
                ["TextBrush"] = "#E6EDF3",
                ["SubTextBrush"] = "#8B98A7",
                ["BorderBrush"] = "#26313D",
                ["SurfaceAltBrush"] = "#151D25",
                ["SurfaceHoverBrush"] = "#19232D",
                ["SurfacePressedBrush"] = "#202C37",
                ["ReadOnlyBrush"] = "#151B22",
                ["DangerBrush"] = "#FB7185",
                ["DangerSurfaceBrush"] = "#351D24",
                ["DangerPressedBrush"] = "#43232C",
                ["DangerBorderBrush"] = "#713242",
                ["SuccessBrush"] = "#4ADE80",
                ["WarningBrush"] = "#FBBF24",
                ["GridHeaderBrush"] = "#151E27",
                ["GridHeaderTextBrush"] = "#AAB7C4",
                ["GridAlternateBrush"] = "#0F161D",
                ["GridLineBrush"] = "#202A35",
                ["RowHoverBrush"] = "#172A2A",
                ["StatusBarBrush"] = "#0E151C",
                ["HintBrush"] = "#102523",
                ["HintBorderBrush"] = "#1F4D47",
                ["SidebarBrush"] = "#080C11",
                ["SidebarHoverBrush"] = "#101820",
                ["SidebarActiveBrush"] = "#11302E",
                ["SidebarBorderBrush"] = "#1B2530",
                ["SidebarTextBrush"] = "#DCE5ED",
                ["SidebarMutedBrush"] = "#718091",
                ["SidebarIconBrush"] = "#9BA8B6",
                ["SidebarIconHoverBrush"] = "#16222D",
                ["SidebarIconPressedBrush"] = "#1D2C38",
                ["SidebarAvatarBrush"] = "#1B2934",
                ["SidebarBrandBrush"] = "#0F766E",
                ["ConsoleBrush"] = "#06090D",
                ["ConsoleAltBrush"] = "#090E13",
                ["ConsoleHeaderBrush"] = "#0D141B",
                ["ConsoleTextBrush"] = "#D6DEE7",
                ["ConsoleMutedBrush"] = "#738293",
                ["ShadowColor"] = "#000000"
            };

        public static AppTheme Current { get; private set; } = AppTheme.Light;

        public static ThemeColorMap Colors { get; } = new();

        public static event Action<AppTheme>? ThemeChanged;

        static ThemeManager()
        {
            Colors.Apply(LightPalette);
        }

        public static void Apply(string? themeName, bool persist = false)
        {
            AppTheme theme = Enum.TryParse(themeName, true, out AppTheme parsed)
                ? parsed
                : AppTheme.Light;
            Apply(theme, persist);
        }

        public static void Apply(AppTheme theme, bool persist = false)
        {
            IReadOnlyDictionary<string, string> palette = theme == AppTheme.Dark
                ? DarkPalette
                : LightPalette;
            Colors.Apply(palette);

            Current = theme;

            if (persist)
            {
                AppConfig config = ConfigHelper.Current;
                config.AppearanceTheme = theme.ToString();
                ConfigHelper.Save(config);
            }

            ThemeChanged?.Invoke(theme);
        }

        public static void Toggle() =>
            Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, persist: true);

        public static string GetDisplayName(AppTheme theme) =>
            theme == AppTheme.Dark ? "深色" : "浅色";
    }

    public sealed class ThemeColorMap : INotifyPropertyChanged
    {
        private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);

        public Color this[string key] => _colors.TryGetValue(key, out Color color)
            ? color
            : Colors.Transparent;

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void Apply(IReadOnlyDictionary<string, string> palette)
        {
            foreach (var pair in palette)
                _colors[pair.Key] = (Color)ColorConverter.ConvertFromString(pair.Value);

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }
}
