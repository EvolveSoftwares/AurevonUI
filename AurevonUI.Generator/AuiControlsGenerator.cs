using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AurevonUI.Generator;

/// <summary>
/// Pro každou třídu dědící z <c>AuiWindow</c> (s <c>base("soubor.svg")</c>) vygeneruje
/// partial třídu se silně typovanými přístupovými vlastnostmi ke Controlům podle id v SVG:
/// <code>public Control Logo => Get("Logo");</code>
/// Přegeneruje se automaticky při každém buildu, tedy i po změně SVG.
/// SVG musí být v projektu jako <c>&lt;AdditionalFiles Include="*.svg" /&gt;</c>.
/// </summary>
[Generator]
public sealed class AuiControlsGenerator : IIncrementalGenerator
{
    private static readonly HashSet<string> VisualTags = new()
    {
        "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "use", "image", "text", "svg", "a",
    };

    private static readonly HashSet<string> NonVisual = new()
    {
        "defs", "clipPath", "mask", "filter", "linearGradient", "radialGradient",
        "pattern", "symbol", "style", "metadata", "title", "desc",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ---- Sync .aui stromu + XSD schématu PŘÍMO V EDITORU ----
        // Spouští se při každé změně .svg/.aui (design-time build v IDE i normální build),
        // takže strom v .aui i IntelliSense vždy odpovídají SVG – bez spouštění aplikace.
        var uiFiles = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase)
                            || f.Path.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => (f.Path, Text: f.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(uiFiles, static (spc, files) => SyncAuiFiles(files));

        // Třídy dědící z AuiWindow s base("...svg")
        var classes = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax c && c.BaseList is not null,
                transform: static (ctx, _) => GetTarget((ClassDeclarationSyntax)ctx.Node))
            .Where(static t => t is not null)
            .Select(static (t, _) => t!.Value);

        // SVG/AUI soubory jako AdditionalFiles -> (cesta, jméno, obsah). Cesta slouží k dočtení
        // ČERSTVÉHO obsahu z disku při emitu (viz níže) – snapshot z IDE může být o krok pozadu.
        var svgFiles = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase)
                            || f.Path.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => (Path: f.Path, Name: Path.GetFileName(f.Path), Text: f.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        var combined = classes.Combine(svgFiles);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var target = pair.Left;
            var files = pair.Right;

            var mainFile = files.FirstOrDefault(f =>
                string.Equals(f.Name, target.SvgName, System.StringComparison.OrdinalIgnoreCase));
            if (mainFile.Name is null)
                return;

            // Čteme ČERSTVÝ obsah z disku (fallback na snapshot z IDE). Re-run generátoru spustí
            // libovolná změna sledovaných .svg/.aui, ale výstup pak vždy odpovídá aktuálním souborům
            // – takže se názvy C# vlastností (CloseButton, Logo, …) přegenerují hned po uložení SVG.
            string mainText = FreshText(mainFile.Path, mainFile.Text);
            if (string.IsNullOrEmpty(mainText))
                return;

            // Rozlišíme svg a .aui (base(...) může ukazovat na kterýkoli z nich) a dohledáme protějšek.
            // Typy vlastností se odvíjejí od SVG tagů + modulů (Type=) z .aui.
            string? svgText = null, auiText = null;
            if (mainFile.Name.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
                auiText = mainText;
            else
                svgText = mainText;

            var companionName = GetCompanionName(mainFile.Name, mainText);
            if (companionName is not null)
            {
                var companion = files.FirstOrDefault(f =>
                    string.Equals(f.Name, companionName, System.StringComparison.OrdinalIgnoreCase));
                if (companion.Name is not null)
                {
                    string companionText = FreshText(companion.Path, companion.Text);
                    if (companionText.Length > 0)
                    {
                        if (companionName.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
                            auiText = companionText;
                        else
                            svgText = companionText;
                    }
                }
            }

            var typed = AuiSyncCore.CollectTypedControls(svgText ?? string.Empty, auiText);

            // Fallback (žádné SVG / jen .aui): vezmeme ids z .aui jako základní Control.
            if (typed.Count == 0 && auiText is not null)
            {
                var ids = new List<string>();
                CollectControlIds(auiText, ids, new HashSet<string>());
                foreach (var id in ids)
                    typed.Add((id, "Control"));
            }

            if (typed.Count == 0)
                return;

            var handlers = CollectEventHandlers(svgText, auiText);
            var src = Emit(target, typed, handlers);
            spc.AddSource($"{target.ClassName}.Controls.g.cs", SourceText.From(src, Encoding.UTF8));
        });
    }

    private readonly struct Target
    {
        public readonly string Namespace;
        public readonly string ClassName;
        public readonly string SvgName;
        public Target(string ns, string className, string svgName)
        {
            Namespace = ns;
            ClassName = className;
            SvgName = svgName;
        }
    }

    private static Target? GetTarget(ClassDeclarationSyntax cls)
    {
        bool inherits = cls.BaseList!.Types.Any(t => LocalName(t.Type) == "AuiWindow");
        if (!inherits)
            return null;

        // base("soubor.svg") v konstruktoru
        string? svg = null;
        foreach (var m in cls.Members)
        {
            if (m is ConstructorDeclarationSyntax ctor
                && ctor.Initializer is { } init
                && init.ThisOrBaseKeyword.Text == "base"
                && init.ArgumentList.Arguments.Count > 0
                && init.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit
                && lit.Token.Value is string s)
            {
                svg = s;
                break;
            }
        }
        if (svg is null)
            return null;

        return new Target(GetNamespace(cls), cls.Identifier.Text, svg);
    }

    private static string LocalName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => type.ToString(),
    };

    private static string GetNamespace(SyntaxNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (p is FileScopedNamespaceDeclarationSyntax fs) return fs.Name.ToString();
            if (p is NamespaceDeclarationSyntax ns) return ns.Name.ToString();
        }
        return string.Empty;
    }

    private static readonly HashSet<string> AuiIgnoreTags = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "defs", "style", "resources", "window", "aui", "aurevonui"
    };

#pragma warning disable RS1035 // designer: zápis .aui/.xsd vedle zdrojů + FileSystemWatcher jsou záměrné

    // ---- Živá synchronizace ----
    // Proces kompilátoru/IDE (Roslyn) žije dál i mimo build, takže tu držíme FileSystemWatcher
    // na složky s .aui/.svg soubory. Uložení SVG v JAKÉMKOLI editoru se tak do .aui a XSD
    // promítne okamžitě – bez rebuildů. Build je jen záchytný bod (EnsureWatcher + první sync).
    private static readonly object WatchLock = new object();
    private static readonly Dictionary<string, FileSystemWatcher> Watchers =
        new Dictionary<string, FileSystemWatcher>(System.StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, System.Threading.Timer> DebounceTimers =
        new Dictionary<string, System.Threading.Timer>(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Vstup z build pipeline: synchronizuje složky projektu a zapne na nich živé watchery.</summary>
    private static void SyncAuiFiles(ImmutableArray<(string Path, string Text)> files)
    {
        var Dirs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var Dir = Path.GetDirectoryName(f.Path);
            if (Dir is not null && Dir.Length > 0)
                Dirs.Add(Dir);
        }
        foreach (var Dir in Dirs)
        {
            SyncDirectory(Dir);
            EnsureWatcher(Dir);
        }
    }

    /// <summary>
    /// Pro každý .aui soubor ve složce: srovná jeho strom se stromem přidruženého SVG
    /// (hodnoty atributů zůstávají) a přegeneruje AurevonUI.xsd pro XML IntelliSense.
    /// Čte se vždy čerstvý obsah z disku; zapisuje se jen při reálné změně obsahu,
    /// takže nevznikají zápisové smyčky.
    /// </summary>
    private static void SyncDirectory(string dir)
    {
        try
        {
            foreach (var auiPath in Directory.GetFiles(dir, "*.aui"))
            {
                try
                {
                    var auiText = AuiSyncCore.TryReadFile(auiPath);
                    if (string.IsNullOrEmpty(auiText))
                        continue;

                    var svgName = GetCompanionName(Path.GetFileName(auiPath), auiText!);
                    if (svgName is null)
                        continue;
                    var svgText = AuiSyncCore.TryReadFile(Path.Combine(dir, svgName));
                    if (string.IsNullOrEmpty(svgText))
                        continue;

                    var synced = AuiSyncCore.ComputeSyncedAui(svgText!, auiText!);
                    if (synced is not null)
                        AuiSyncCore.WriteIfChanged(auiPath, synced);

                    // XSD schéma vedle .aui – targetNamespace podle namespace .aui souboru.
                    var ns = AuiSyncCore.ParseXmlLenient(synced ?? auiText!).Root?.Name.NamespaceName ?? string.Empty;
                    var ids = AuiSyncCore.CollectSvgControls(AuiSyncCore.ParseXmlLenient(svgText!))
                        .Select(c => c.Id).ToList();
                    var xsdPath = Path.Combine(dir, "AurevonUI.xsd");
                    AuiSyncCore.WriteIfChanged(xsdPath, AuiSyncCore.GenerateXsd(ids, ns));
                }
                catch
                {
                    // rozepsaný/zamčený/nevalidní soubor – synchronizuje se při další změně
                }
            }
        }
        catch
        {
            // složka nedostupná – zkusí se při dalším buildu
        }
    }

    /// <summary>Zapne živý watcher na složce (jen jednou; přežívá mezi buildy v procesu Roslynu).</summary>
    private static void EnsureWatcher(string dir)
    {
        lock (WatchLock)
        {
            if (Watchers.ContainsKey(dir))
                return;
            try
            {
                var Watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                void OnChange(object sender, FileSystemEventArgs e)
                {
                    if (e.Name is null)
                        return;
                    if (!e.Name.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase) &&
                        !e.Name.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
                        return;
                    ScheduleSync(dir);
                }
                Watcher.Changed += OnChange;
                Watcher.Created += OnChange;
                Watcher.Renamed += (s, e) => OnChange(s, e);
                Watcher.EnableRaisingEvents = true;
                Watchers[dir] = Watcher;
            }
            catch
            {
                // bez watcheru zůstává sync alespoň při buildu
            }
        }
    }

    /// <summary>Naplánuje sync složky s krátkým odstupem (debounce – editor soubor dopíše).</summary>
    private static void ScheduleSync(string dir)
    {
        lock (WatchLock)
        {
            if (DebounceTimers.TryGetValue(dir, out var Timer))
                Timer.Change(350, System.Threading.Timeout.Infinite);
            else
                DebounceTimers[dir] = new System.Threading.Timer(
                    _ => SyncDirectory(dir), null, 350, System.Threading.Timeout.Infinite);
        }
    }

#pragma warning restore RS1035

    /// <summary>Čerstvý obsah souboru z disku (fallback na snapshot z IDE, když se nedá číst).</summary>
    private static string FreshText(string? path, string fallback)
    {
        if (!string.IsNullOrEmpty(path))
        {
            var disk = AuiSyncCore.TryReadFile(path!);
            if (!string.IsNullOrEmpty(disk))
                return disk!;
        }
        return fallback ?? string.Empty;
    }

    /// <summary>Element (podle id) je šablona pro ItemsControl – id obsahuje „Template". Nemá typovanou property.</summary>
    private static bool IsTemplateId(string? id) =>
        id is not null && id.IndexOf("template", System.StringComparison.OrdinalIgnoreCase) >= 0;

    private static XDocument LoadXml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// K .aui souboru vrátí jméno přidruženého .svg (atribut <c>Svg</c> na kořeni, jinak stejné
    /// jméno s koncovkou .svg) a naopak k .svg vrátí jméno .aui. Jinak null.
    /// </summary>
    private static string? GetCompanionName(string name, string text)
    {
        if (name.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var root = LoadXml(text).Root;
                var svgAttr = root?.Attributes().FirstOrDefault(a =>
                    string.Equals(a.Name.LocalName, "Svg", System.StringComparison.OrdinalIgnoreCase));
                if (svgAttr is not null)
                    return Path.GetFileName(svgAttr.Value);
            }
            catch { }
            return Path.ChangeExtension(name, ".svg");
        }
        if (name.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(name, ".aui");
        return null;
    }

    /// <summary>Posbírá id vizuálních elementů – podporuje jak SVG soubory, tak AUI metadatové šablony.</summary>
    private static void CollectControlIds(string fileText, List<string> result, HashSet<string> seen)
    {
        XDocument doc;
        try
        {
            doc = LoadXml(fileText);
        }
        catch
        {
            return;
        }

        var root = doc.Root;
        if (root is null)
            return;

        bool isAui = !string.Equals(root.Name.LocalName, "svg", System.StringComparison.OrdinalIgnoreCase);

        if (isAui)
        {
            // V .aui souboru bereme tag name nebo explicitní id/Name
            foreach (var el in root.Descendants())
            {
                if (AuiIgnoreTags.Contains(el.Name.LocalName))
                    continue;

                var id = (string?)el.Attribute("id") ?? (string?)el.Attribute("Id") ?? (string?)el.Attribute("Name");
                if (id is null && !el.Name.LocalName.Equals("Control", System.StringComparison.OrdinalIgnoreCase))
                    id = el.Name.LocalName;
                if (id is null || IsTemplateId(id) || seen.Contains(id))
                    continue;

                seen.Add(id);
                result.Add(id);
            }
        }
        else
        {
            // V .svg souboru filtrujeme podle vizuálních tagů, defs a šablon
            foreach (var el in root.Descendants())
            {
                var id = (string?)el.Attribute("id");
                if (id is null || seen.Contains(id))
                    continue;
                if (!VisualTags.Contains(el.Name.LocalName))
                    continue;
                if (el.Ancestors().Any(a => NonVisual.Contains(a.Name.LocalName)))
                    continue;
                if (el.AncestorsAndSelf().Any(a => IsTemplateId((string?)a.Attribute("id"))))
                    continue;

                seen.Add(id);
                result.Add(id);
            }
        }
    }

    private static List<string> CollectEventHandlers(string? svgText, string? auiText)
    {
        var handlers = new HashSet<string>(System.StringComparer.Ordinal);
        void Extract(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var doc = LoadXml(text!);
                foreach (var el in doc.Descendants())
                {
                    foreach (var attr in el.Attributes())
                    {
                        var name = attr.Name.LocalName;
                        if (string.Equals(name, "Click", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "Press", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "HoverEnter", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "HoverLeave", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "Submit", System.StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(attr.Value))
                                handlers.Add(attr.Value.Trim());
                        }
                    }
                }
            }
            catch {}
        }
        Extract(svgText);
        Extract(auiText);
        return handlers.ToList();
    }

    private static string Emit(Target target, List<(string Id, string TypeName)> typed, List<string> eventHandlers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        bool hasNs = target.Namespace.Length > 0;
        string indent = hasNs ? "    " : "";
        if (hasNs)
        {
            sb.Append("namespace ").Append(target.Namespace).AppendLine();
            sb.AppendLine("{");
        }

        sb.Append(indent).AppendLine("[global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]");
        sb.Append(indent).Append("partial class ").Append(target.ClassName).AppendLine();
        sb.Append(indent).AppendLine("{");

        if (eventHandlers.Count > 0)
        {
            sb.Append(indent).AppendLine("    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"Preserve event handlers\")]");
            foreach (var handler in eventHandlers)
            {
                sb.Append(indent).Append("    [global::System.Diagnostics.CodeAnalysis.DynamicDependency(\"").Append(Escape(handler)).AppendLine("\")]");
            }
            sb.Append(indent).AppendLine("    private void __PreserveEventHandlers() {}");
            sb.AppendLine();
        }

        var usedNames = new HashSet<string>();
        foreach (var (id, typeName) in typed)
        {
            string name = SafeIdentifier(id);
            string unique = name;
            int n = 2;
            while (!usedNames.Add(unique))
                unique = name + (n++);

            string fullType = AuiSyncCore.FullTypeName(typeName);
            sb.Append(indent).Append("    /// <summary>Control z SVG: id=\"").Append(Escape(id))
              .Append("\" (typ ").Append(typeName).AppendLine(").</summary>");
            sb.Append(indent).Append("    public ").Append(fullType).Append(' ').Append(unique);
            if (typeName == "Control")
                sb.Append(" => Get(\"").Append(Escape(id)).AppendLine("\");");
            else
                sb.Append(" => Get<").Append(fullType).Append(">(\"").Append(Escape(id)).AppendLine("\");");
        }

        sb.Append(indent).AppendLine("}");
        if (hasNs)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string SafeIdentifier(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char ch in id)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

        string s = sb.ToString();
        if (s.Length == 0)
            s = "_";
        if (char.IsDigit(s[0]))
            s = "_" + s;
        if (CSharpKeywords.Contains(s))
            s = "@" + s;
        return s;
    }

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };
}
