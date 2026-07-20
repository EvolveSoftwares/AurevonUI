// Sdílený soubor: kompiluje se do AurevonUI.Generator (sync přímo v editoru při každé
// změně .svg/.aui) i do AurevonUI runtime (záložní sync v DEBUG – externí editory).
// Díky tomu existuje JEDNA implementace a výstupy se nikdy nerozejdou.
#pragma warning disable RS1035 // zápis .aui/.xsd vedle zdrojových souborů je záměrné chování designeru

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AurevonUI.Generator;

/// <summary>
/// Jádro synchronizace: strom .aui souboru se generuje a udržuje podle stromu SVG
/// (nové controly pod správného rodiče v pořadí dokumentu, přesuny se zachováním atributů
/// i komentářů, mazání zaniklých) a k tomu XSD schéma pro XML IntelliSense.
/// Vše se dynamicky odvíjí od SVG souboru.
/// </summary>
internal static class AuiSyncCore
{
    private static readonly HashSet<string> VisualTags = new HashSet<string>
    {
        "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "use", "image", "text", "svg", "a",
    };

    private static readonly HashSet<string> NonVisualTags = new HashSet<string>
    {
        "defs", "clipPath", "mask", "filter", "linearGradient", "radialGradient",
        "pattern", "symbol", "style", "metadata", "title", "desc",
    };

    private static readonly HashSet<string> AuiIgnoreTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "style", "resources", "window", "aui", "aurevonui"
    };

    // SVG tag -> automatická typová třída controlu (AurevonUI.Elements.*).
    private static readonly Dictionary<string, string> TagTypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "g", "Group" }, { "svg", "Group" },
        { "path", "Path" }, { "text", "TextControl" },
        { "rect", "Rect" }, { "circle", "Circle" }, { "ellipse", "Ellipse" },
        { "line", "Line" }, { "polyline", "Polyline" }, { "polygon", "Polygon" },
        { "image", "Image" },
    };

    // Vyšší moduly – přiřazují se přes atribut Type="…" a smí sedět jen na <g>.
    // Klíč = hodnota v .aui (co uživatel napíše), hodnota = název třídy.
    private static readonly Dictionary<string, string> ModuleTypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Text", "TextControl" }, { "TextControl", "TextControl" },
        { "TextBox", "TextBox" }, { "ScrollViewer", "ScrollViewer" },
    };

    /// <summary>Hodnoty atributu Type nabízené v IntelliSense (co uživatel píše do .aui).</summary>
    public static IEnumerable<string> ModuleNames => ModuleTypeNames.Keys;

    /// <summary>
    /// Určí typovou třídu controlu: modul z atributu <c>Type=</c> (jen na <c>&lt;g&gt;</c>) má
    /// přednost, jinak automaticky podle SVG tagu. Neznámé → základní <c>Control</c>.
    /// Stejná pravidla používá generátor (emise vlastností) i runtime (factory) – nikdy se nerozejdou.
    /// </summary>
    public static string ResolveControlTypeName(string? TypeAttr, string? SvgTag)
    {
        if (!string.IsNullOrWhiteSpace(TypeAttr)
            && ModuleTypeNames.TryGetValue(TypeAttr!.Trim(), out var Module)
            && (SvgTag is null || SvgTag.Equals("g", StringComparison.OrdinalIgnoreCase)
                                || SvgTag.Equals("svg", StringComparison.OrdinalIgnoreCase)))
        {
            return Module;
        }
        if (SvgTag is not null && TagTypeNames.TryGetValue(SvgTag, out var Tag))
            return Tag;
        return "Control";
    }

    /// <summary>Plně kvalifikovaný název typu pro generovaný kód.</summary>
    public static string FullTypeName(string TypeName) =>
        TypeName == "Control" ? "global::AurevonUI.Control" : "global::AurevonUI.Elements." + TypeName;

    // Hodnoty enumů pro XSD – drž v souladu s enumy HAlign/VAlign/AuiStretch/Cursor v AurevonUI.
    private static readonly string[] HAlignValues = { "Left", "Center", "Right", "Stretch" };
    private static readonly string[] VAlignValues = { "Top", "Center", "Bottom", "Stretch" };
    private static readonly string[] StretchValues = { "None", "Uniform", "Fill" };
    private static readonly string[] CursorValues =
    {
        "Default", "Arrow", "Hand", "Text", "Wait", "Crosshair", "No", "SizeAll", "SizeNS", "SizeWE",
    };


    /// <summary>
    /// Naparsuje XML bez ohledu na DOCTYPE (SVG exporty ho často mají) a bez externích entit.
    /// Nevýznamný whitespace se zahazuje, takže serializace vždy vyjde hezky odsazená
    /// (xml:space="preserve" v SVG zůstává respektováno).
    /// </summary>
    public static XDocument ParseXmlLenient(string Xml)
    {
        var Settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
        };
        using var Reader = XmlReader.Create(new StringReader(Xml), Settings);
        return XDocument.Load(Reader);
    }

    /// <summary>Element (podle id) je šablona pro ItemsControl – id obsahuje „Template".</summary>
    public static bool IsTemplateId(string? Id) =>
        Id is not null && Id.IndexOf("template", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Logické id elementu v .aui: atribut <c>id</c>/<c>Id</c>/<c>Name</c>, jinak název tagu
    /// (<c>&lt;Logo /&gt;</c> = "Logo"). Ignorované tagy a generický <c>&lt;Control&gt;</c>
    /// bez Name logické id nemají.
    /// </summary>
    public static string? LogicalAuiId(XElement El)
    {
        var Id = (string?)El.Attribute("id") ?? (string?)El.Attribute("Id") ?? (string?)El.Attribute("Name");
        if (Id is not null)
            return Id;
        var Tag = El.Name.LocalName;
        if (AuiIgnoreTags.Contains(Tag) || Tag.Equals("Control", StringComparison.OrdinalIgnoreCase))
            return null;
        return Tag;
    }

    /// <summary>Smí se id ze SVG použít přímo jako název tagu v .aui? Jinak se zapíše jako &lt;Control Name="…"/&gt;.</summary>
    public static bool IsUsableTagName(string Id)
    {
        if (AuiIgnoreTags.Contains(Id) || Id.Equals("Control", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            XmlConvert.VerifyNCName(Id);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Controly ze SVG jako (Id, IdRodiče) v pořadí dokumentu. Stejná pravidla jako runtime:
    /// jen vizuální elementy s id, mimo defs/clipPath/… a mimo šablony.
    /// </summary>
    public static List<(string Id, string? ParentId)> CollectSvgControls(XDocument SvgDoc)
    {
        var Result = new List<(string Id, string? ParentId)>();
        var Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var Root = SvgDoc.Root;
        if (Root is null)
            return Result;

        foreach (var El in Root.Descendants())
        {
            var Id = (string?)El.Attribute("id");
            if (Id is null || Seen.Contains(Id)) continue;
            if (!VisualTags.Contains(El.Name.LocalName)) continue;
            if (El.Ancestors().Any(A => NonVisualTags.Contains(A.Name.LocalName))) continue;
            if (El.AncestorsAndSelf().Any(A => IsTemplateId((string?)A.Attribute("id")))) continue;

            // Rodič = nejbližší předek, který je sám controlem (v doc pořadí už zpracovaný).
            string? ParentId = El.Ancestors()
                .Select(A => (string?)A.Attribute("id"))
                .FirstOrDefault(Pid => Pid is not null && Seen.Contains(Pid));

            Result.Add((Id, ParentId));
            Seen.Add(Id);
        }
        return Result;
    }

    /// <summary>
    /// Controly jako (Id, NázevTypu) v pořadí dokumentu SVG. Typ = modul z <c>Type=</c> v .aui
    /// (jen na <c>&lt;g&gt;</c>), jinak automaticky podle SVG tagu. Používá generátor k emisi
    /// silně typovaných vlastností. <paramref name="AuiText"/> smí být null (pak jen tagy).
    /// </summary>
    public static List<(string Id, string TypeName)> CollectTypedControls(string SvgText, string? AuiText)
    {
        var Result = new List<(string Id, string TypeName)>();
        var Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Type= atributy z .aui podle logického id.
        var TypeById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(AuiText))
        {
            try
            {
                var AuiRoot = ParseXmlLenient(AuiText!).Root;
                if (AuiRoot is not null)
                    foreach (var El in AuiRoot.DescendantsAndSelf())
                    {
                        var Id = LogicalAuiId(El);
                        var T = (string?)El.Attribute("Type") ?? (string?)El.Attribute("type");
                        if (Id is not null && T is not null && !TypeById.ContainsKey(Id))
                            TypeById[Id] = T;
                    }
            }
            catch { }
        }

        // Control elementy ze SVG (pořadí dokumentu) + jejich tag → typ.
        try
        {
            var Root = ParseXmlLenient(SvgText).Root;
            if (Root is not null)
                foreach (var El in Root.Descendants())
                {
                    var Id = (string?)El.Attribute("id");
                    if (Id is null || Seen.Contains(Id)) continue;
                    if (!VisualTags.Contains(El.Name.LocalName)) continue;
                    if (El.Ancestors().Any(A => NonVisualTags.Contains(A.Name.LocalName))) continue;
                    if (El.AncestorsAndSelf().Any(A => IsTemplateId((string?)A.Attribute("id")))) continue;

                    Seen.Add(Id);
                    TypeById.TryGetValue(Id, out var TypeAttr);
                    Result.Add((Id, ResolveControlTypeName(TypeAttr, El.Name.LocalName)));
                }
        }
        catch { }

        // Id jen v .aui (např. <Control Name="…"/>) – typ z Type= nebo základní Control.
        foreach (var Kvp in TypeById)
        {
            if (Seen.Contains(Kvp.Key)) continue;
            Seen.Add(Kvp.Key);
            Result.Add((Kvp.Key, ResolveControlTypeName(Kvp.Value, null)));
        }

        return Result;
    }

    /// <summary>
    /// Vrátí kanonický obsah .aui zrcadlící stromovou strukturu SVG (null jen při nevalidním vstupu).
    /// Nové controly se vkládají pod svého rodiče v pořadí dokumentu, špatně zanořené elementy
    /// se přesunou (atributy, potomci i přilehlý komentář zůstávají – hodnoty se nikdy neztrácí)
    /// a controly, které v SVG už nejsou, se odstraní. Zapisuj přes <see cref="WriteIfChanged"/> –
    /// ta soubor přepíše jen při reálné změně.
    /// </summary>
    public static string? ComputeSyncedAui(string SvgText, string AuiText)
    {
        var SvgDoc = ParseXmlLenient(SvgText);
        var AuiDoc = ParseXmlLenient(AuiText);
        var AuiRoot = AuiDoc.Root;
        if (AuiRoot is null || SvgDoc.Root is null)
            return null;

        var SvgControls = CollectSvgControls(SvgDoc);
        var SvgIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var SvgOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int I = 0; I < SvgControls.Count; I++)
        {
            SvgIdSet.Add(SvgControls[I].Id);
            SvgOrder[SvgControls[I].Id] = I;
        }

        // Existující deklarace v .aui podle logického id (tag nebo id/Id/Name atribut)
        var Declared = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var El in AuiRoot.Descendants())
        {
            var Id = LogicalAuiId(El);
            if (Id is not null && !Declared.ContainsKey(Id))
                Declared[Id] = El;
        }

        // Každý control ze SVG existuje a sedí pod správným rodičem.
        // Rodiče jdou v pořadí dokumentu vždy před potomky, takže Declared[ParentId] už existuje.
        foreach (var (Id, ParentId) in SvgControls)
        {
            var ParentEl = ParentId is null ? AuiRoot : Declared[ParentId];
            if (!Declared.TryGetValue(Id, out var El))
            {
                El = IsUsableTagName(Id)
                    ? new XElement(XName.Get(Id, AuiRoot.Name.NamespaceName))
                    : new XElement(XName.Get("Control", AuiRoot.Name.NamespaceName), new XAttribute("Name", Id));
                InsertInSvgOrder(ParentEl, El, SvgOrder[Id], SvgOrder);
                Declared[Id] = El;
            }
            else if (El.Parent != ParentEl)
            {
                var Comment = El.PreviousNode as XComment; // popisek elementu se stěhuje s ním
                Comment?.Remove();
                El.Remove();
                InsertInSvgOrder(ParentEl, El, SvgOrder[Id], SvgOrder);
                if (Comment is not null) El.AddBeforeSelf(Comment);
            }
        }

        // Odstraníme controly, které už v SVG nejsou (živí potomci se přesunuli výše).
        foreach (var Kvp in Declared)
        {
            if (SvgIdSet.Contains(Kvp.Key)) continue;
            var El = Kvp.Value;
            if (El.Parent is null) continue; // už odstraněn spolu s rodičem
            (El.PreviousNode as XComment)?.Remove();
            El.Remove();
        }

        // Kanonický tvar se vrací vždy – i beze změn stromu se tím srovná odsazení/formát;
        // WriteIfChanged pak zapíše jen pokud se výsledek od souboru skutečně liší.
        return Serialize(AuiDoc);
    }

    /// <summary>
    /// Vloží element mezi sourozence podle pořadí v SVG dokumentu: za posledního sourozence,
    /// který je v SVG dřív. Pořadí existujících (uživatelem seřazených) elementů se nemění.
    /// </summary>
    private static void InsertInSvgOrder(XElement ParentEl, XElement El, int MyOrder, Dictionary<string, int> SvgOrder)
    {
        XElement? After = null;
        foreach (var Sib in ParentEl.Elements())
        {
            if (Sib == El) continue;
            var Sid = LogicalAuiId(Sib);
            if (Sid is not null && SvgOrder.TryGetValue(Sid, out var So) && So < MyOrder)
                After = Sib;
        }
        if (After is not null)
            After.AddAfterSelf(El);
        else
            ParentEl.AddFirst(El);
    }

    /// <summary>
    /// Vygeneruje XSD schéma pro XML IntelliSense: každý control ze SVG je globální element
    /// a smí být zanořený v libovolném jiném controlu (strom zrcadlí SVG). targetNamespace
    /// odpovídá namespace .aui souboru, takže editor schéma opravdu napáruje.
    /// </summary>
    public static string GenerateXsd(IReadOnlyList<string> ControlIds, string TargetNamespace)
    {
        var Sb = new StringBuilder();
        Sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        Sb.AppendLine("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"");
        if (TargetNamespace.Length > 0)
        {
            Sb.AppendLine($"           targetNamespace=\"{TargetNamespace}\"");
            Sb.AppendLine($"           xmlns=\"{TargetNamespace}\"");
        }
        Sb.AppendLine("           elementFormDefault=\"qualified\">");
        Sb.AppendLine();

        AppendXsdEnum(Sb, "HAlignType", HAlignValues);
        AppendXsdEnum(Sb, "VAlignType", VAlignValues);
        AppendXsdEnum(Sb, "StretchType", StretchValues);
        AppendXsdEnum(Sb, "CursorType", CursorValues);
        AppendXsdEnum(Sb, "WindowStyleType", new[] { "Window", "None" });
        AppendXsdEnum(Sb, "StartupLocationType", new[] { "Manual", "CenterScreen" });
        AppendXsdEnum(Sb, "TypeModuleType", ModuleNames.ToArray());

        // Bool: v IntelliSense se nabízí jen True/False (výčet), ale přes union s patternem
        // jsou validní i true/false/1/0 – runtime parsuje všechny varianty.
        Sb.AppendLine("  <xs:simpleType name=\"BoolType\">");
        Sb.AppendLine("    <xs:union>");
        Sb.AppendLine("      <xs:simpleType>");
        Sb.AppendLine("        <xs:restriction base=\"xs:string\">");
        Sb.AppendLine("          <xs:enumeration value=\"True\" />");
        Sb.AppendLine("          <xs:enumeration value=\"False\" />");
        Sb.AppendLine("        </xs:restriction>");
        Sb.AppendLine("      </xs:simpleType>");
        Sb.AppendLine("      <xs:simpleType>");
        Sb.AppendLine("        <xs:restriction base=\"xs:string\">");
        Sb.AppendLine("          <xs:pattern value=\"true|false|1|0\" />");
        Sb.AppendLine("        </xs:restriction>");
        Sb.AppendLine("      </xs:simpleType>");
        Sb.AppendLine("    </xs:union>");
        Sb.AppendLine("  </xs:simpleType>");
        Sb.AppendLine();

        // Id nepoužitelná jako název tagu se v .aui zapisují jako <Control Name="…"/>.
        var ElementNames = ControlIds.Where(IsUsableTagName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Sb.AppendLine("  <xs:complexType name=\"ControlType\">");
        Sb.AppendLine("    <xs:choice minOccurs=\"0\" maxOccurs=\"unbounded\">");
        Sb.AppendLine("      <xs:element ref=\"Control\" />");
        foreach (var Name in ElementNames)
        {
            Sb.AppendLine($"      <xs:element ref=\"{Name}\" />");
        }
        Sb.AppendLine("    </xs:choice>");
        Sb.AppendLine("    <xs:attribute name=\"Name\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Type\" type=\"TypeModuleType\" />");
        Sb.AppendLine("    <xs:attribute name=\"HorizontalAlignment\" type=\"HAlignType\" />");
        Sb.AppendLine("    <xs:attribute name=\"VerticalAlignment\" type=\"VAlignType\" />");
        Sb.AppendLine("    <xs:attribute name=\"OffsetX\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"OffsetY\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"Margin\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"MarginPercent\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Cursor\" type=\"CursorType\" />");
        Sb.AppendLine("    <xs:attribute name=\"Opacity\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"Scale\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"StretchToWindow\" type=\"BoolType\" />");
        Sb.AppendLine("    <xs:attribute name=\"IsHittable\" type=\"BoolType\" />");
        Sb.AppendLine("    <xs:attribute name=\"IsEnabled\" type=\"BoolType\" />");
        Sb.AppendLine("    <xs:attribute name=\"Visible\" type=\"BoolType\" />");
        Sb.AppendLine("    <xs:attribute name=\"Fill\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Stroke\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"StrokeWidth\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"Placeholder\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Text\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollPaddingTop\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollPaddingBottom\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollbarVisible\" type=\"BoolType\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollbarWidth\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollbarPadding\" type=\"xs:float\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollbarColor\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"ScrollbarTrackColor\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Click\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Press\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"HoverEnter\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"HoverLeave\" type=\"xs:string\" />");
        Sb.AppendLine("    <xs:attribute name=\"Submit\" type=\"xs:string\" />");
        Sb.AppendLine("  </xs:complexType>");
        Sb.AppendLine();
        Sb.AppendLine("  <xs:element name=\"Control\" type=\"ControlType\" />");
        foreach (var Name in ElementNames)
        {
            Sb.AppendLine($"  <xs:element name=\"{Name}\" type=\"ControlType\" />");
        }
        Sb.AppendLine();
        Sb.AppendLine("  <xs:element name=\"Window\">");
        Sb.AppendLine("    <xs:complexType>");
        Sb.AppendLine("      <xs:choice minOccurs=\"0\" maxOccurs=\"unbounded\">");
        Sb.AppendLine("        <xs:element ref=\"Control\" />");
        foreach (var Name in ElementNames)
        {
            Sb.AppendLine($"        <xs:element ref=\"{Name}\" />");
        }
        Sb.AppendLine("      </xs:choice>");
        Sb.AppendLine("      <xs:attribute name=\"Svg\" type=\"xs:string\" use=\"required\" />");
        Sb.AppendLine("      <xs:attribute name=\"HorizontalAlignment\" type=\"HAlignType\" />");
        Sb.AppendLine("      <xs:attribute name=\"VerticalAlignment\" type=\"VAlignType\" />");
        Sb.AppendLine("      <xs:attribute name=\"Stretch\" type=\"StretchType\" />");
        Sb.AppendLine("      <xs:attribute name=\"Title\" type=\"xs:string\" />");
        Sb.AppendLine("      <xs:attribute name=\"Width\" type=\"xs:int\" />");
        Sb.AppendLine("      <xs:attribute name=\"Height\" type=\"xs:int\" />");
        Sb.AppendLine("      <xs:attribute name=\"Icon\" type=\"xs:string\" />");
        Sb.AppendLine("      <xs:attribute name=\"WindowStyle\" type=\"WindowStyleType\" />");
        Sb.AppendLine("      <xs:attribute name=\"WindowStartupLocation\" type=\"StartupLocationType\" />");
        Sb.AppendLine("    </xs:complexType>");
        Sb.AppendLine("  </xs:element>");
        Sb.AppendLine();
        Sb.AppendLine("</xs:schema>");
        return Sb.ToString();
    }

    private static void AppendXsdEnum(StringBuilder Sb, string TypeName, string[] Values)
    {
        Sb.AppendLine($"  <xs:simpleType name=\"{TypeName}\">");
        Sb.AppendLine("    <xs:restriction base=\"xs:string\">");
        foreach (var V in Values)
        {
            Sb.AppendLine($"      <xs:enumeration value=\"{V}\" />");
        }
        Sb.AppendLine("    </xs:restriction>");
        Sb.AppendLine("  </xs:simpleType>");
        Sb.AppendLine();
    }

    /// <summary>Zapíše soubor jen když se obsah reálně liší – brání zápisovým smyčkám (generátor ↔ watcher).</summary>
    public static bool WriteIfChanged(string FilePath, string Content)
    {
        try
        {
            if (File.Exists(FilePath) && File.ReadAllText(FilePath) == Content)
                return false;
        }
        catch
        {
            // nejde přečíst – zkusíme rovnou zapsat
        }
        File.WriteAllText(FilePath, Content, new UTF8Encoding(false));
        return true;
    }

    /// <summary>Přečte soubor i když ho právě drží otevřený jiný program (editor při ukládání). Null při chybě.</summary>
    public static string? TryReadFile(string FilePath)
    {
        try
        {
            using var Fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var Sr = new StreamReader(Fs);
            return Sr.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    /// <summary>Deterministická serializace dokumentu (utf-8 deklarace, standardní odsazení).</summary>
    public static string Serialize(XDocument Doc)
    {
        using var Sw = new Utf8StringWriter();
        Doc.Save(Sw);
        return Sw.ToString();
    }
}
