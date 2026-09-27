using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using AlchemistPayTrack.Presentation;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Shared visual styling for every exported/printed document (payroll
    /// reports, receipts, and anything else added later): the brand color
    /// gradient, a rendered gradient banner image for PDF headers, the
    /// company details block (from Settings) that belongs on every
    /// exportable, and the existing developer watermark/footer.
    /// </summary>
    public static class ExportBranding
    {
        // Matches the app icon and UI theme's indigo brand color.
        // Fully qualified: both System.Windows.Media (WPF, used for the
        // gradient rendering below) and QuestPDF.Infrastructure (used
        // throughout this file for PDF styling) declare a "Color" type, so
        // the unqualified name is ambiguous wherever both usings are in
        // scope in the same file.
        public static readonly System.Windows.Media.Color BrandStart = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#4F46E5");
        public static readonly System.Windows.Media.Color BrandEnd = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#4338CA");
        public static readonly string BrandHexStart = "#4F46E5";
        public static readonly string BrandHexEnd = "#4338CA";

        public static void ApplyPdfWatermark(PageDescriptor page)
        {
            page.Background()
                .AlignCenter()
                .AlignMiddle()
                .Rotate(-45)
                .Text(AppBranding.WatermarkShort)
                .FontSize(48)
                .FontColor(QuestPDF.Helpers.Colors.Grey.Lighten3);
        }

        public static void ApplyExcelWatermark(IXLWorksheet sheet, int columnCount)
        {
            var centerColumn = System.Math.Max(1, (columnCount + 1) / 2);
            var cell = sheet.Cell(12, centerColumn);
            cell.Value = AppBranding.WatermarkShort;
            cell.Style.Font.FontSize = 36;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.LightGray;
            cell.Style.Alignment.SetTextRotation(45);
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        public static void ApplyExcelFooter(IXLWorksheet sheet, int row, int columnCount)
        {
            sheet.Cell(row, 1).Value = AppBranding.ExportFooterLine;
            sheet.Cell(row, 1).Style.Font.FontSize = 8;
            sheet.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;
            sheet.Range(row, 1, row, columnCount).Merge();
        }

        /// <summary>
        /// Renders a horizontal brand-color gradient as a PNG, for use as a
        /// PDF page/section header banner. QuestPDF's own Background() only
        /// takes a flat color, so the gradient is rendered once here (via
        /// WPF's own drawing surface, no extra imaging package needed) and
        /// then placed as an image with text layered on top.
        /// Must be called on the UI thread (export/print commands in this
        /// app are synchronous RelayCommands invoked directly from a button
        /// click, so this is always satisfied without extra plumbing).
        /// </summary>
        public static byte[] CreateGradientBannerPng(int width, int height)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new LinearGradientBrush(BrandStart, BrandEnd, new Point(0, 0), new Point(1, 0));
                dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// The gradient banner header used at the top of every generated
        /// PDF: company name and the document title in white, over the
        /// brand gradient. Company details (contact/address/registration)
        /// follow just below in the document body via CompanyDetailsLine.
        /// </summary>
        public static void ApplyGradientHeader(ColumnDescriptor column, AppSettingsData settings, string documentTitle, string? subtitle = null)
        {
            var bannerPng = CreateGradientBannerPng(1000, 130);

            column.Item().Height(80).Layers(layers =>
            {
                layers.PrimaryLayer().Image(bannerPng).FitArea();

                layers.Layer().Padding(14).Row(row =>
                {
                    row.RelativeItem().Column(text =>
                    {
                        text.Item().Text(settings.CompanyName).FontSize(20).Bold().FontColor(QuestPDF.Helpers.Colors.White);
                        text.Item().Text(documentTitle).FontSize(11).FontColor(QuestPDF.Helpers.Colors.White);
                    });

                    if (!string.IsNullOrWhiteSpace(subtitle))
                    {
                        row.ConstantItem(220).AlignRight().AlignMiddle()
                            .Text(subtitle).FontSize(10).FontColor(QuestPDF.Helpers.Colors.White);
                    }
                });
            });
        }

        /// <summary>
        /// The one-line company contact/registration strip shown just
        /// under the gradient header on every export, so every exported
        /// document - not just payroll reports - carries the company's
        /// details rather than only the developer watermark.
        /// </summary>
        public static void ApplyCompanyDetailsLine(ColumnDescriptor column, AppSettingsData settings)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(settings.CompanyAddress)) parts.Add(settings.CompanyAddress);
            if (!string.IsNullOrWhiteSpace(settings.CompanyPhone)) parts.Add($"Tel: {settings.CompanyPhone}");
            if (!string.IsNullOrWhiteSpace(settings.CompanyEmail)) parts.Add(settings.CompanyEmail);
            if (!string.IsNullOrWhiteSpace(settings.CompanyRegistrationNumber)) parts.Add($"Reg: {settings.CompanyRegistrationNumber}");

            if (parts.Count > 0)
            {
                column.Item().PaddingTop(4).PaddingBottom(8).Text(string.Join("   |   ", parts))
                    .FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
            }
        }

        /// <summary>
        /// Applies a professional brand-color header row (solid indigo,
        /// white bold text) plus alternating row banding to an Excel sheet.
        /// True gradients aren't a native Excel cell-fill concept the way
        /// they are in a rendered PDF/image, so a clean solid brand color
        /// plus banding is the idiomatic "professional" Excel look.
        /// </summary>
        public static void StyleHeaderRow(IXLWorksheet sheet, int row, int columnCount)
        {
            var range = sheet.Range(row, 1, row, columnCount);
            range.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandHexStart);
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Row(row).Height = 20;
        }

        public static void ApplyRowBanding(IXLWorksheet sheet, int firstDataRow, int lastDataRow, int columnCount)
        {
            for (int r = firstDataRow; r <= lastDataRow; r++)
            {
                if ((r - firstDataRow) % 2 == 1)
                {
                    sheet.Range(r, 1, r, columnCount).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FF");
                }
                sheet.Range(r, 1, r, columnCount).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                sheet.Range(r, 1, r, columnCount).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                sheet.Range(r, 1, r, columnCount).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E5E7EB");
                sheet.Range(r, 1, r, columnCount).Style.Border.InsideBorderColor = XLColor.FromHtml("#E5E7EB");
            }
        }

        /// <summary>
        /// Writes the company name, contact, and registration details into
        /// the top rows of an Excel sheet, so every workbook - not just the
        /// data - carries the company's identity, matching the PDF header.
        /// Returns the next free row after the block.
        /// </summary>
        public static int WriteExcelCompanyHeader(IXLWorksheet sheet, AppSettingsData settings, string documentTitle, int columnCount)
        {
            sheet.Cell(1, 1).Value = settings.CompanyName;
            sheet.Cell(1, 1).Style.Font.FontSize = 16;
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml(BrandHexStart);
            sheet.Range(1, 1, 1, columnCount).Merge();

            sheet.Cell(2, 1).Value = documentTitle;
            sheet.Cell(2, 1).Style.Font.FontSize = 12;
            sheet.Cell(2, 1).Style.Font.Italic = true;
            sheet.Range(2, 1, 2, columnCount).Merge();

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(settings.CompanyAddress)) parts.Add(settings.CompanyAddress);
            if (!string.IsNullOrWhiteSpace(settings.CompanyPhone)) parts.Add($"Tel: {settings.CompanyPhone}");
            if (!string.IsNullOrWhiteSpace(settings.CompanyEmail)) parts.Add(settings.CompanyEmail);
            if (!string.IsNullOrWhiteSpace(settings.CompanyRegistrationNumber)) parts.Add($"Reg: {settings.CompanyRegistrationNumber}");

            int row = 3;
            if (parts.Count > 0)
            {
                sheet.Cell(3, 1).Value = string.Join("   |   ", parts);
                sheet.Cell(3, 1).Style.Font.FontSize = 9;
                sheet.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;
                sheet.Range(3, 1, 3, columnCount).Merge();
                row = 4;
            }

            return row + 1; // blank spacer row before the data header
        }
    }
}
