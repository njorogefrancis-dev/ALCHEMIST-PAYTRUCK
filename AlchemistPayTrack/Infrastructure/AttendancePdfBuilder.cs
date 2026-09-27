using System;
using System.Collections.Generic;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using AlchemistPayTrack.Data;
using AlchemistPayTrack.Presentation;

namespace AlchemistPayTrack.Infrastructure
{
    /// <summary>
    /// Builds a branded PDF of today's attendance - present and absent
    /// worker lists - sharing the same gradient header and company details
    /// block as every other export.
    /// </summary>
    public static class AttendancePdfBuilder
    {
        public static void Generate(IReadOnlyList<Worker> present, IReadOnlyList<Worker> absent, AppSettingsData settings, string outputPath)
        {
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(0);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    ExportBranding.ApplyPdfWatermark(page);

                    page.Content().Column(column =>
                    {
                        ExportBranding.ApplyGradientHeader(column, settings, "Daily Attendance Report", DateTime.Now.ToString("dddd, dd MMMM yyyy"));

                        column.Item().PaddingHorizontal(30).PaddingTop(14).Column(body =>
                        {
                            ExportBranding.ApplyCompanyDetailsLine(body, settings);

                            body.Item().Row(row =>
                            {
                                row.RelativeItem().Background("#F0FDF4").Padding(10).Column(c =>
                                {
                                    c.Item().Text("PRESENT").FontSize(9).Bold().FontColor("#15803D");
                                    c.Item().Text(present.Count.ToString()).FontSize(20).Bold().FontColor("#15803D");
                                });
                                row.ConstantItem(12);
                                row.RelativeItem().Background("#FEF2F2").Padding(10).Column(c =>
                                {
                                    c.Item().Text("ABSENT").FontSize(9).Bold().FontColor("#B91C1C");
                                    c.Item().Text(absent.Count.ToString()).FontSize(20).Bold().FontColor("#B91C1C");
                                });
                            });

                            body.Item().PaddingTop(18).PaddingBottom(6).Text("Present").FontSize(13).Bold().FontColor("#15803D");
                            body.Item().Element(c => BuildWorkerTable(c, present, "No workers currently present."));

                            body.Item().PaddingTop(20).PaddingBottom(6).Text("Absent").FontSize(13).Bold().FontColor("#B91C1C");
                            body.Item().Element(c => BuildWorkerTable(c, absent, "No workers currently absent - everyone is present."));
                        });
                    });

                    page.Footer().PaddingTop(10).Column(footer =>
                    {
                        footer.Item().AlignCenter().Text(x =>
                        {
                            x.Span("Generated ");
                            x.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                        });
                        footer.Item().PaddingTop(2).AlignCenter().Text(AppBranding.ExportFooterLine)
                            .FontSize(7).FontColor(QuestPDF.Helpers.Colors.Grey.Medium);
                    });
                });
            }).GeneratePdf(outputPath);
        }

        private static void BuildWorkerTable(IContainer container, IReadOnlyList<Worker> workers, string emptyMessage)
        {
            if (workers.Count == 0)
            {
                container.Text(emptyMessage).FontSize(9).Italic().FontColor(QuestPDF.Helpers.Colors.Grey.Darken1);
                return;
            }

            container.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2f);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(1.5f);
                });

                table.Header(header =>
                {
                    string[] headers = { "Full Name", "Job Role", "Category", "Phone" };
                    foreach (var h in headers)
                    {
                        header.Cell().Background(ExportBranding.BrandHexStart).Padding(5)
                            .Text(h).SemiBold().FontColor(QuestPDF.Helpers.Colors.White);
                    }
                });

                bool alternate = false;
                foreach (var worker in workers)
                {
                    var bg = alternate ? "#F9FAFB" : "#FFFFFF";
                    alternate = !alternate;

                    table.Cell().Background(bg).Border(1).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten2).Padding(4).Text(worker.FullName);
                    table.Cell().Background(bg).Border(1).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten2).Padding(4).Text(worker.JobRole);
                    table.Cell().Background(bg).Border(1).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten2).Padding(4).Text(worker.WorkerCategory);
                    table.Cell().Background(bg).Border(1).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten2).Padding(4).Text(worker.PhoneNumber);
                }
            });
        }
    }
}
