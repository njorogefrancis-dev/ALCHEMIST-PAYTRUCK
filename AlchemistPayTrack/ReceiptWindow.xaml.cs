using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using AlchemistPayTrack.Infrastructure;
using AlchemistPayTrack.Presentation;

namespace AlchemistPayTrack
{
    /// <summary>
    /// Displays a payment receipt in-app, and can print it (via WPF's own
    /// PrintVisual - no PDF round-trip needed for that) or export it to a
    /// PDF file with the same gradient branding as every other export.
    /// </summary>
    public partial class ReceiptWindow : Window
    {
        public ReceiptViewModel Receipt { get; }

        /// <summary>Print/Export both require PrintPayslips - a receipt is
        /// this app's payslip/proof-of-payment. Viewing it in-app needs no
        /// separate permission (whoever can see the Payments page already
        /// has RecordPayments, which is a strictly higher bar).</summary>
        public bool CanPrint { get; }

        public string FooterLine => AppBranding.ExportFooterLine;

        public ReceiptWindow(ReceiptViewModel receipt, bool canPrint = true)
        {
            InitializeComponent();
            Receipt = receipt;
            CanPrint = canPrint;
            DataContext = this;
        }

        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return;

                // Measure/arrange the print area against the printable page
                // size before handing it to PrintVisual, otherwise WPF
                // prints it at its on-screen layout size rather than fitted
                // to the page.
                var pageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
                PrintArea.Measure(pageSize);
                PrintArea.Arrange(new Rect(new Point(0, 0), pageSize));

                dialog.PrintVisual(PrintArea, $"Receipt {Receipt.ReceiptNumber}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Printing failed: {UserMessage.For(ex)}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "PDF Document (*.pdf)|*.pdf",
                    FileName = $"Receipt_{Receipt.ReceiptNumber}.pdf"
                };

                if (dialog.ShowDialog() != true) return;

                ReceiptPdfBuilder.Generate(Receipt, dialog.FileName);

                MessageBox.Show($"Receipt exported to {Path.GetFileName(dialog.FileName)}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {UserMessage.For(ex)}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
