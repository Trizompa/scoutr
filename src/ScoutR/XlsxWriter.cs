using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ScoutR;

/// <summary>Minimal .xlsx writer (one sheet, bold header, indent per row): no Excel needed.</summary>
public static class XlsxWriter
{
    /// <summary>A cell: text or number. <paramref name="Indent"/> indents the text (0 = none).</summary>
    public sealed record Cell(object? Value, int Indent = 0);

    public static void Write(string path, string sheetName, IReadOnlyList<string> header,
        IReadOnlyList<IReadOnlyList<Cell>> rows, IReadOnlyList<double> widths)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }

        Add("[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        Add("_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Add("xl/workbook.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Esc(sheetName.Length > 31 ? sheetName[..31] : sheetName)}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Add("xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");

        // Styles: 0 normal, 1 bold header, 2.. = indent 1, 2, 3...
        var xfs = new StringBuilder("""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>""");
        for (int i = 1; i <= 15; i++)
            xfs.Append($"""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment indent="{i}"/></xf>""");
        Add("xl/styles.xml",
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE8EEF7"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="17">{xfs}</cellXfs></styleSheet>""");

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sheet.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sheet.Append("<cols>");
        for (int c = 0; c < widths.Count; c++)
            sheet.Append($"""<col min="{c + 1}" max="{c + 1}" width="{widths[c].ToString(System.Globalization.CultureInfo.InvariantCulture)}" customWidth="1"/>""");
        sheet.Append("</cols><sheetData>");
        sheet.Append("""<row r="1">""");
        for (int c = 0; c < header.Count; c++) sheet.Append(TextCell(c, 1, header[c], 1));
        sheet.Append("</row>");
        for (int r = 0; r < rows.Count; r++)
        {
            int rn = r + 2;
            sheet.Append($"""<row r="{rn}">""");
            for (int c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c];
                int style = cell.Indent > 0 ? 1 + Math.Min(cell.Indent, 15) : 0;
                sheet.Append(cell.Value switch
                {
                    int or double or float or long =>
                        $"""<c r="{Ref(c, rn)}"{(style > 0 ? $" s=\"{style}\"" : "")}><v>{Convert.ToString(cell.Value, System.Globalization.CultureInfo.InvariantCulture)}</v></c>""",
                    _ => TextCell(c, rn, Convert.ToString(cell.Value) ?? "", style),
                });
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");
        if (header.Count > 0) sheet.Append($"""<autoFilter ref="A1:{Ref(header.Count - 1, Math.Max(1, rows.Count + 1))}"/>""");
        sheet.Append("</worksheet>");
        Add("xl/worksheets/sheet1.xml", sheet.ToString());
    }

    private static string TextCell(int col, int row, string text, int style) =>
        text.Length == 0 ? "" :
        $"""<c r="{Ref(col, row)}" t="inlineStr"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space="preserve">{Esc(text)}</t></is></c>""";

    private static string Ref(int col, int row)
    {
        string name = "";
        for (int c = col + 1; c > 0; c = (c - 1) / 26) name = (char)('A' + (c - 1) % 26) + name;
        return name + row;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
