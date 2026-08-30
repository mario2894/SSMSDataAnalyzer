using System;
using System.Drawing;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// CONTRACT.md / lead's field note on the tool window's find feature: a hardcoded
    /// highlight colour is illegible in one theme or the other. That fix used WPF
    /// {DynamicResource EnvironmentColors...} brushes — not available here, since
    /// GridControl paints with GDI+ (System.Drawing.Color / SolidBrush), not WPF. The GDI
    /// equivalent of the same theme system is <see cref="VSColorTheme.GetThemedColor"/>,
    /// which resolves the SAME <see cref="EnvironmentColors"/> keys directly to a
    /// System.Drawing.Color — no manual light/dark branching needed, VS does it.
    /// </summary>
    internal static class GridThemeColors
    {
        /// <summary>Solid, VS-theme-aware colour for the CURRENT match's own text, if we ever
        /// need to paint it ourselves — normally unnecessary, since the current match is just
        /// the grid's own SELECTED cell and SSMS paints that highlight itself for free.</summary>
        public static Color HighlightText => VSColorTheme.GetThemedColor(EnvironmentColors.SystemHighlightTextColorKey);

        /// <summary>
        /// "Other matches" (not the current one) get a lighter tint of the same highlight
        /// hue blended toward the grid's own window background — same "current match full
        /// strength, others lighter" convention as the tool window's find, just computed as a
        /// solid blended colour instead of WPF opacity (GDI SolidBrush has no independent
        /// alpha-over-arbitrary-content compositing the way a WPF Brush with Opacity does).
        /// </summary>
        public static Color OtherMatchBackground()
        {
            var highlight = VSColorTheme.GetThemedColor(EnvironmentColors.SystemHighlightColorKey);
            var window = VSColorTheme.GetThemedColor(EnvironmentColors.SystemWindowColorKey);
            return Blend(highlight, window, 0.35);
        }

        public static Color OtherMatchText => VSColorTheme.GetThemedColor(EnvironmentColors.SystemWindowTextColorKey);

        private static Color Blend(Color a, Color b, double aWeight)
        {
            aWeight = Math.Max(0, Math.Min(1, aWeight));
            double bWeight = 1 - aWeight;
            return Color.FromArgb(
                (int)(a.R * aWeight + b.R * bWeight),
                (int)(a.G * aWeight + b.G * bWeight),
                (int)(a.B * aWeight + b.B * bWeight));
        }
    }
}
