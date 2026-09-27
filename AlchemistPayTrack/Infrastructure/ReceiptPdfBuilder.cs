using System.Collections.Generic;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using AlchemistPayTrack.Presentation;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Builds the PDF version of a payment receipt, sharing the same
    /// gradient header and company-details block as every other export
    /// (ExportBranding) so the printed/exported receipt matches what's
    /// shown on screen in ReceiptWindow.
    /// </summary>
    public static class ReceiptPdfBuilder
    {
        public static void Generate(ReceiptViewModel receipt, string outputPath)
        {
            BuildDocument(new[] { receipt }).GeneratePdf(outputPath);
        }

        /// <summary>Builds one PDF containing every given receipt, one per
        /// page, so a batch of receipts can be printed or exported as a
        /// single job instead of one-at-a-time.</summary>
        public static void GenerateBulk(IEnumerable<ReceiptViewModel> receipts, string outputPath)
        {
            BuildDocument(receipts).GeneratePdf(outputPath);
        }

        private static QuestPDF.Infrastructure.IDocument BuildDocument(IEnumerable<ReceiptViewModel> receipts)
        {
            return Document.Create(container =>
            {
                foreach (var receipt in receipts)
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A5);
                        page.Margin(0);
                        page.DefaultTextStyle(x => x.FontSize(10));

                        page.Content().Column(column =>
                        {
                            ExportBranding.ApplyGradientHeader(column, receipt.Settings, "PAYMENT RECEIPT");

                            column.Item().PaddingHorizontal(24).PaddingTop(14).Column(body =>
                            {
                                ExportBranding.ApplyCompanyDetailsLine(body, receipt.Settings);

                                body.Item().Row(row =>
                                {
                                    row.RelativeItem().Column(c =>
                                    {
                                        c.Item().Text("RECEIPT NO.").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                        c.Item().Text(receipt.ReceiptNumber).FontSize(13).Bold();
                                    });
                                    row.RelativeItem().AlignRight().Column(c =>
                                    {
                                        c.Item().AlignRight().Text("DATE").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                        c.Item().AlignRight().Text(receipt.FormattedDate).FontSize(11);
                                    });
                                });

                                body.Item().PaddingVertical(10).LineHorizontal(1).LineColor(QuestPDF.Helpers.Colors.Grey.Lighten2);

                                body.Item().Text("PAID TO").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                body.Item().Text(receipt.WorkerName).FontSize(15).Bold();
                                body.Item().PaddingBottom(12).Text($"Worker ID: {receipt.WorkerId}   |   Role: {receipt.JobRoleDisplay}")
                                    .FontSize(9).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);

                                body.Item().Background("#EEF2FF").Padding(14).Column(c =>
                                {
                                    c.Item().Text("AMOUNT PAID").FontSize(8).FontColor(ExportBranding.BrandHexStart);
                                    c.Item().Text(receipt.FormattedAmount).FontSize(24).Bold().FontColor(ExportBranding.BrandHexEnd);
                                });

                                body.Item().PaddingTop(12).Row(row =>
                                {
                                    row.RelativeItem().Column(c =>
                                    {
                                        c.Item().Text("PAYMENT METHOD").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                        c.Item().Text(receipt.PaymentMethod).FontSize(11);
                                    });

                                    if (receipt.HasMpesaCode)
                                    {
                                        row.RelativeItem().AlignRight().Column(c =>
                                        {
                                            c.Item().AlignRight().Text("M-PESA CODE").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                            c.Item().AlignRight().Text(receipt.MpesaCodeDisplay).FontSize(11);
                                        });
                                    }
                                });

                                body.Item().PaddingVertical(16).LineHorizontal(1).LineColor(QuestPDF.Helpers.Colors.Grey.Lighten2);

                                body.Item().Row(row =>
                                {
                                    row.RelativeItem().PaddingRight(10).Column(c =>
                                    {
                                        c.Item().PaddingTop(20).LineHorizontal(1).LineColor(QuestPDF.Helpers.Colors.Grey.Lighten1);
                                        c.Item().AlignCenter().Text("Received by").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                    });
                                    row.RelativeItem().PaddingLeft(10).Column(c =>
                                    {
                                        c.Item().PaddingTop(20).LineHorizontal(1).LineColor(QuestPDF.Helpers.Colors.Grey.Lighten1);
                                        c.Item().AlignCenter().Text("Authorized by").FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                                    });
                                });

                                body.Item().PaddingTop(14).AlignCenter().Text($"Recorded by {receipt.RecordedBy}")
                                    .FontSize(8).FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                            });
                        });

                        page.Footer().PaddingTop(10).Column(footer =>
                        {
                            footer.Item().AlignCenter().Text(AppBranding.ExportFooterLine)
                                .FontSize(6).FontColor(QuestPDF.Helpers.Colors.Grey.Medium);
                        });
                    });
                }
            });
        }
    }
}
