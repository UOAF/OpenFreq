using Avalonia.Media;

public static class MaterialColorUtil
{
    private static readonly Color[] Palette =
    [
        // 500 shades
        Color.Parse("#F44336"), // Red
        Color.Parse("#E91E63"), // Pink
        Color.Parse("#9C27B0"), // Purple
        Color.Parse("#673AB7"), // Deep Purple
        Color.Parse("#3F51B5"), // Indigo
        Color.Parse("#2196F3"), // Blue
        Color.Parse("#03A9F4"), // Light Blue
        Color.Parse("#00BCD4"), // Cyan
        Color.Parse("#009688"), // Teal
        Color.Parse("#4CAF50"), // Green
        Color.Parse("#8BC34A"), // Light Green
        Color.Parse("#CDDC39"), // Lime
        Color.Parse("#FFEB3B"), // Yellow
        Color.Parse("#FFC107"), // Amber
        Color.Parse("#FF9800"), // Orange
        Color.Parse("#FF5722"), // Deep Orange
        Color.Parse("#795548"), // Brown
        Color.Parse("#9E9E9E"), // Grey
        Color.Parse("#607D8B"), // Blue Grey
        // 700 shades (darker variants)
        Color.Parse("#D32F2F"), // Red 700
        Color.Parse("#C2185B"), // Pink 700
        Color.Parse("#7B1FA2"), // Purple 700
        Color.Parse("#512DA8"), // Deep Purple 700
        Color.Parse("#303F9F"), // Indigo 700
        Color.Parse("#1976D2"), // Blue 700
        Color.Parse("#0288D1"), // Light Blue 700
        Color.Parse("#0097A7"), // Cyan 700
        Color.Parse("#00796B"), // Teal 700
        Color.Parse("#388E3C"), // Green 700
        Color.Parse("#F57C00"), // Orange 700
        Color.Parse("#E64A19"), // Deep Orange 700
        // A400 accents (vivid/electric)
        Color.Parse("#D500F9"), // Purple A400
        Color.Parse("#651FFF"), // Deep Purple A400
        Color.Parse("#3D5AFE"), // Indigo A400
        Color.Parse("#00E5FF"), // Cyan A400
        Color.Parse("#1DE9B6"), // Teal A400
        Color.Parse("#00E676"), // Green A400
        Color.Parse("#FF3D00"), // Deep Orange A400
    ];

    private static int StableHash(string s)
    {
        unchecked
        {
            int hash = (int)2166136261;
            foreach (var c in s) { hash ^= c; hash *= 16777619; }
            return hash;
        }
    }

    public static IBrush GetAccentBrush(string name) =>
        new SolidColorBrush(Palette[(StableHash(name) & 0x7FFFFFFF) % Palette.Length]);
}
