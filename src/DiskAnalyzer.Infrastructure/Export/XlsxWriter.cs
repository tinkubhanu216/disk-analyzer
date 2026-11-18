using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Infrastructure.Export;

/// <summary>
/// Minimal streaming SpreadsheetML (.xlsx) writer: rows are written straight into the zip entry
/// with inline strings, so memory stays flat regardless of row count. Enforces Excel's row limit.
/// </summary>
internal static class XlsxWriter
{
    public const int MaxDataRows = 1_048_575; // Excel limit 1,048,576 minus the header row

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int StyleHeader = 1;
    private const int StyleBytes = 2;
    private const int StyleDate = 3;
    private const int StylePercent = 4;

    public static ExportSummary Write(string path, ScanResult result, IReadOnlyList<int> nodes, Func<ScanResult, int, ExportRow> buildRow, IProgress<double>? progress, CancellationToken ct)
    {
        int rows = Math.Min(nodes.Count, MaxDataRows);
        bool truncated = nodes.Count > MaxDataRows;

        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteText(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            WriteText(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteText(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                <sheets><sheet name="Items" sheetId="1" r:id="rId1"/><sheet name="Summary" sheetId="2" r:id="rId2"/></sheets>
                </workbook>
                """);
            WriteText(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteText(zip, "xl/styles.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="{MainNs}">
                <numFmts count="2"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm"/><numFmt numFmtId="165" formatCode="0.00%"/></numFmts>
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="5">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                <xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                </cellXfs>
                <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                </styleSheet>
                """);

            ZipArchiveEntry sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
            using (Stream stream = sheet.Open())
            using (XmlWriter xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false }))
            {
                xml.WriteStartDocument(true);
                xml.WriteStartElement("worksheet", MainNs);
                xml.WriteStartElement("sheetViews");
                xml.WriteStartElement("sheetView");
                xml.WriteAttributeString("workbookViewId", "0");
                xml.WriteStartElement("pane");
                xml.WriteAttributeString("ySplit", "1");
                xml.WriteAttributeString("topLeftCell", "A2");
                xml.WriteAttributeString("activePane", "bottomLeft");
                xml.WriteAttributeString("state", "frozen");
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();

                double[] widths = [70, 32, 8, 10, 13, 16, 16, 12, 60, 18, 10, 10, 10, 10, 10];
                xml.WriteStartElement("cols");
                for (int c = 0; c < widths.Length; c++)
                {
                    xml.WriteStartElement("col");
                    xml.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("width", widths[c].ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("customWidth", "1");
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();

                xml.WriteStartElement("sheetData");
                xml.WriteStartElement("row");
                foreach (string header in ExportService.Headers)
                {
                    WriteString(xml, header, StyleHeader);
                }

                xml.WriteEndElement();

                for (int n = 0; n < rows; n++)
                {
                    if ((n & 0x3FFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report((double)n / rows);
                    }

                    ExportRow row = buildRow(result, nodes[n]);
                    xml.WriteStartElement("row");
                    WriteString(xml, row.Path);
                    WriteString(xml, row.Name);
                    WriteString(xml, row.IsDirectory ? "Folder" : "File");
                    WriteString(xml, row.Extension);
                    WriteString(xml, row.Category);
                    WriteNumber(xml, row.SizeBytes, StyleBytes);
                    WriteNumber(xml, row.AllocatedBytes, StyleBytes);
                    WriteString(xml, SizeFormatter.Format(row.SizeBytes, CultureInfo.InvariantCulture));
                    WriteString(xml, row.ParentFolder);
                    if (row.LastModifiedUtc is { } modified)
                    {
                        WriteNumber(xml, modified.ToOADate(), StyleDate);
                    }
                    else
                    {
                        WriteString(xml, string.Empty);
                    }

                    if (row.IsDirectory)
                    {
                        WriteNumber(xml, row.FileCount, StyleBytes);
                        WriteNumber(xml, row.FolderCount, StyleBytes);
                    }
                    else
                    {
                        WriteString(xml, string.Empty);
                        WriteString(xml, string.Empty);
                    }

                    WriteNumber(xml, row.PercentOfDrive / 100, StylePercent);
                    WriteNumber(xml, row.PercentOfParent / 100, StylePercent);
                    WriteNumber(xml, row.PercentOfScan / 100, StylePercent);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement(); // sheetData
                xml.WriteEndElement(); // worksheet
            }

            WriteSummarySheet(zip, result, rows, truncated);
        }

        progress?.Report(1);
        string? note = truncated ? $"Excel supports at most {MaxDataRows + 1:N0} rows; the first {MaxDataRows:N0} of {nodes.Count:N0} items were exported. Use CSV or JSON for the full list." : null;
        return new ExportSummary(path, rows, truncated, note);
    }

    private static void WriteSummarySheet(ZipArchive zip, ScanResult result, int rows, bool truncated)
    {
        ZipArchiveEntry sheet = zip.CreateEntry("xl/worksheets/sheet2.xml", CompressionLevel.Fastest);
        using Stream stream = sheet.Open();
        using XmlWriter xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", MainNs);
        xml.WriteStartElement("cols");
        xml.WriteStartElement("col");
        xml.WriteAttributeString("min", "1");
        xml.WriteAttributeString("max", "1");
        xml.WriteAttributeString("width", "28");
        xml.WriteAttributeString("customWidth", "1");
        xml.WriteEndElement();
        xml.WriteStartElement("col");
        xml.WriteAttributeString("min", "2");
        xml.WriteAttributeString("max", "2");
        xml.WriteAttributeString("width", "60");
        xml.WriteAttributeString("customWidth", "1");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("sheetData");

        void Row(string label, Action value)
        {
            xml.WriteStartElement("row");
            WriteString(xml, label, StyleHeader);
            value();
            xml.WriteEndElement();
        }

        Row("Scanned path", () => WriteString(xml, result.RootPath));
        Row("Result type", () => WriteString(xml, result.SourceDescription));
        Row("Scan date (UTC)", () => WriteNumber(xml, result.StartedUtc.ToOADate(), StyleDate));
        Row("Status", () => WriteString(xml, result.Status.ToString()));
        Row("Scanner", () => WriteString(xml, result.ProviderName));
        Row("Total size (bytes)", () => WriteNumber(xml, result.TotalBytes, StyleBytes));
        Row("Total size on disk (bytes)", () => WriteNumber(xml, result.TotalAllocatedBytes, StyleBytes));
        Row("Files", () => WriteNumber(xml, result.TotalFiles, StyleBytes));
        Row("Folders", () => WriteNumber(xml, result.TotalFolders, StyleBytes));
        Row("Drive capacity (bytes)", () => WriteNumber(xml, result.DriveCapacityBytes, StyleBytes));
        Row("Rows exported", () => WriteNumber(xml, rows, StyleBytes));
        if (truncated)
        {
            Row("Note", () => WriteString(xml, "Truncated to Excel's row limit; export CSV/JSON for all items."));
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteString(XmlWriter xml, string value, int style = 0)
    {
        xml.WriteStartElement("c");
        xml.WriteAttributeString("t", "inlineStr");
        if (style != 0)
        {
            xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        }

        xml.WriteStartElement("is");
        xml.WriteStartElement("t");
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteString(Sanitize(value));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteNumber(XmlWriter xml, double value, int style)
    {
        xml.WriteStartElement("c");
        xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        xml.WriteElementString("v", value.ToString("R", CultureInfo.InvariantCulture));
        xml.WriteEndElement();
    }

    /// <summary>Removes characters that are illegal in XML 1.0 (possible in NTFS names).</summary>
    private static string Sanitize(string value)
    {
        foreach (char c in value)
        {
            if (!XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c))
            {
                return string.Concat(value.Where(ch => XmlConvert.IsXmlChar(ch) || char.IsSurrogate(ch)));
            }
        }

        return value;
    }

    private static void WriteText(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }
}
