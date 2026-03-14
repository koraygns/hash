using System;
using System.Windows;

namespace hashadli
{
    /// <summary>
    /// Ayarlar penceresi - Kullanıcı hangi hash algoritmalarının hesaplanacağını seçer
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public bool UseMD5 { get; private set; }
        public bool UseSHA1 { get; private set; }
        
        public string PdfTitle { get; private set; }
        public string PdfFileNumber { get; private set; }
        public string PdfOrganization { get; private set; }
        public bool PdfShowDateTime { get; private set; }

        public SettingsWindow(bool useMD5, bool useSHA1,
            string pdfTitle, string pdfFileNumber, string pdfOrganization, bool pdfShowDateTime)
        {
            InitializeComponent();
            
            // Mevcut ayarları yükle
            chkMD5.IsChecked = useMD5;
            chkSHA1.IsChecked = useSHA1;
            
            txtPdfTitle.Text = pdfTitle ?? "";
            txtPdfFileNumber.Text = pdfFileNumber ?? "";
            txtPdfOrganization.Text = pdfOrganization ?? "";
            chkPdfShowDateTime.IsChecked = pdfShowDateTime;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // En az bir hash seçilmiş mi kontrol et
            if (!chkMD5.IsChecked.Value && !chkSHA1.IsChecked.Value)
            {
                MessageBox.Show("En az bir hash algoritması seçmelisiniz!", "Uyarı", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Başlık zorunlu alan kontrolü
            if (string.IsNullOrWhiteSpace(txtPdfTitle.Text))
            {
                MessageBox.Show("PDF başlığı zorunludur!", "Uyarı", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                txtPdfTitle.Focus();
                return;
            }

            // Ayarları kaydet
            UseMD5 = chkMD5.IsChecked.Value;
            UseSHA1 = chkSHA1.IsChecked.Value;
            
            PdfTitle = txtPdfTitle.Text.Trim();
            PdfFileNumber = txtPdfFileNumber.Text.Trim();
            PdfOrganization = txtPdfOrganization.Text.Trim();
            PdfShowDateTime = chkPdfShowDateTime.IsChecked ?? true;

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
