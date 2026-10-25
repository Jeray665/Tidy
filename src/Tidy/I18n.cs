using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Tidy;

/// <summary>
/// English without touching every page: Spanish is the source text, and in English each text is looked up as it is shown
/// (Assets/i18n/en.json, kept complete by tools/i18n.py). Interpolated texts are templates whose holes are wildcards,
/// translated on their own. In Spanish none of this runs.
/// </summary>
public static class I18n
{
    public static bool English { get; private set; }
    static readonly Dictionary<string, string> Exact = [];
    static readonly Dictionary<string, string> Lower = [];
    static readonly List<(string Anchor, Regex Re, string En)> Templates = [];
    static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>"es", "en" or "" (same as Windows: Spanish if Windows is in Spanish, English otherwise).</summary>
    public static string Resolve(string? setting) =>
        setting is "es" or "en" ? setting : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    public static void Init(string? setting)
    {
        English = Resolve(setting) == "en";
        if (!English) return;
        using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/i18n/en.json")).Stream)
            foreach (var (es, en) in JsonSerializer.Deserialize<Dictionary<string, string>>(s)!)
            {
                if (en == "" || en == es) continue;
                if (!es.Contains("{0}")) { Exact[es] = en; Lower.TryAdd(es.ToLowerInvariant(), en); continue; }
                var parts = Regex.Split(es, @"\{\d+\}");
                var pattern = "^" + string.Join("(.+?)", parts.Select(Regex.Escape)) + "$";
                Templates.Add((parts.OrderByDescending(p => p.Length).First(), new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant), en));
            }
        // most specific first, so "{0} · {1}" only catches what nothing better does
        Templates.Sort((a, b) => b.Re.ToString().Length.CompareTo(a.Re.ToString().Length));
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = culture;
        // Loaded only reaches elements with their own handler; every element gets SizeChanged on its first layout
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnFirstLayout));
    }

    static readonly DependencyProperty DoneProperty = DependencyProperty.RegisterAttached("Done", typeof(bool), typeof(I18n));

    public static string T(string s) => !English || s.Length < 2 ? s : Cache.GetOrAdd(s, Translate);

    static string Translate(string s)
    {
        if (Cache.Count > 20_000) Cache.Clear(); // ponytail: crude cap; live numbers make endless new strings
        if (Exact.TryGetValue(s, out var e)) return e;
        // names lowercased in code to sit mid-sentence ("Prioridad: alta"): translate the original, lowercase its first letter
        if (s == s.ToLowerInvariant() && Lower.TryGetValue(s, out var l)) return char.ToLowerInvariant(l[0]) + l[1..];
        if (!Regex.IsMatch(s, "[a-záéíóúñ]{2}")) return s; // numbers, units, names in caps
        foreach (var (anchor, re, en) in Templates)
        {
            if (!s.Contains(anchor, StringComparison.Ordinal)) continue;
            var m = re.Match(s);
            if (!m.Success) continue;
            try { return string.Format(en, m.Groups.Cast<Group>().Skip(1).Select(g => (object)T(g.Value)).ToArray()); }
            catch (FormatException) { return s; }
        }
        // texts glued together in code: lines, " · " lists, "Hello, name", "3 changes"
        foreach (var sep in (string[])["\n", " · ", ", "])
            if (s.Contains(sep)) return string.Join(sep, s.Split(sep).Select(T));
        return Regex.Match(s, @"^([\d.,]+ )(.+)$") is { Success: true } n ? n.Groups[1].Value + T(n.Groups[2].Value) : s;
    }

    static void OnFirstLayout(object sender, SizeChangedEventArgs e)
    {
        var el = (FrameworkElement)sender;
        if ((bool)el.GetValue(DoneProperty)) return;
        el.SetValue(DoneProperty, true);
        switch (el)
        {
            case TextBlock tb when tb.Inlines.Count > 1 || tb.Inlines.FirstInline is { } i && i is not Run:
                foreach (var run in tb.Inlines.OfType<Run>().ToList()) run.SetCurrentValue(Run.TextProperty, T(run.Text));
                break;
            case TextBlock or AccessText:
                Watch(el);
                // own handlers, so this element does get Loaded/Unloaded from now on
                el.Loaded += (_, _) => Watch(el);
                el.Unloaded += (_, _) => Descriptor(el).RemoveValueChanged(el, OnChanged);
                break;
            case Wpf.Ui.Controls.TextBox box: box.PlaceholderText = T(box.PlaceholderText ?? ""); break;
            case Window w: w.Title = T(w.Title); break;
        }
        if (el.ToolTip is string tip) el.ToolTip = T(tip);
    }

    // text set later from code (statuses, counters) is translated as it changes
    static void Watch(FrameworkElement el)
    {
        Apply(el);
        var d = Descriptor(el);
        d.RemoveValueChanged(el, OnChanged);
        d.AddValueChanged(el, OnChanged);
    }

    static DependencyProperty Prop(object el) => el is AccessText ? AccessText.TextProperty : TextBlock.TextProperty;
    static DependencyPropertyDescriptor Descriptor(FrameworkElement el) => DependencyPropertyDescriptor.FromProperty(Prop(el), el.GetType());
    static void OnChanged(object? sender, EventArgs e) { if (sender is FrameworkElement el) Apply(el); }

    static void Apply(FrameworkElement el)
    {
        var dp = Prop(el);
        var v = el.GetValue(dp) as string ?? "";
        var t = T(v);
        if (t != v) el.SetCurrentValue(dp, t);
    }
}
