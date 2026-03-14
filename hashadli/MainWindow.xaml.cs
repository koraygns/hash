using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Printing;

namespace hashadli
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<HashResult> hashResults;
        private ObservableCollection<HashResult> allHashResults;
        private ObservableCollection<HashResult> compareResults;
        private ObservableCollection<HashResult> allCompareResults;
        private Dictionary<string, HashResult> comparisonDict;
        
        // Hash cache - Aynı klasörün hash'ini tekrar hesaplamayı önlemek için
        private Dictionary<string, (string MD5, string SHA1)> folderHashCache = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        
        // Dinamik dosya listesi için
        private ObservableCollection<FileItem> fileItems;
        private ObservableCollection<HashResultItem> hashResultItems;
        
        // Hesaplanan dosyaları takip etmek için (tekrar hesaplamayı önlemek için)
        private HashSet<string> calculatedFiles;

        // Hash algoritması ayarları - Ayarlardan yüklenir
        private bool useMD5;
        private bool useSHA1;
        
        // PDF ayarları
        private string pdfTitle = "Hash Sonuçları";
        private string pdfFileNumber = "";
        private string pdfOrganization = "";
        private bool pdfShowDateTime = true;

        // Progress tracking - İlerleme takibi için
        private int totalFiles = 0;
        private int processedFiles = 0;
        
        // Klasör hash hesaplama için base klasör yolu
        private string baseFolderPath = "";
        
        // Filtreleme ve arama değişkenleri
        private string currentSearchText = "";
        private string currentFilterCompare = "All";
        
        // Performans için - Kolon genişlik ayarlama debouncing
        private System.Windows.Threading.DispatcherTimer resizeTimer;
        
        // Karşılaştırma DataGrid için debouncing
        private System.Windows.Threading.DispatcherTimer compareResizeTimer;
        // İki dosya karşılaştırma sonuçları
        private ObservableCollection<FileCompareRow> fileCompareResults;
        
        // Tab ve işlem takibi - Progress panel'i doğru tab'da göstermek için
        private string activeTabHeader = "";
        private bool isProcessingFolderHash = false;
        private bool isProcessingCompare = false;
        
        // İptal mekanizması için
        private System.Threading.CancellationTokenSource cancellationTokenSource;
        
        // İptal edildiğinde UI güncellemelerini engellemek için
        private volatile bool isCancellationRequested = false;
        
        // Performans için - Batch UI güncellemeleri (Milyarlarca dosya için optimize edildi)
        private int uiUpdateCounter = 0;
        // ULTRA OPTİMİZE - 4-5TB veri setleri için UI güncellemelerini minimize et
        private const int UI_UPDATE_BATCH_SIZE = 5000; // Her 5000 dosyada bir progress güncelle (büyük veri setleri için)
        private const int UI_ADD_BATCH_SIZE = 20000; // Her 20000 dosyada bir toplu ekleme (4-5TB veri setleri için kritik)
        
        // Büyük diskler için batch buffer - UI thread çağrılarını minimize eder
        private List<HashResult> pendingHashResults = new List<HashResult>();
        
        // Tüm sonuçları topla - UI güncellemesini en sona bırakmak için
        private List<HashResult> allCollectedResults = new List<HashResult>();
        
        // İki klasör karşılaştırma için batch buffer - UI thread çağrılarını minimize eder
        private List<HashResult> pendingCompareResults = new List<HashResult>();
        
        // İki klasör karşılaştırma için tüm sonuçları topla - UI güncellemesini en sona bırakmak için
        private List<HashResult> allCollectedCompareResults = new List<HashResult>();
        
        // Progress güncelleme throttling - Büyük veri setleri için daha az güncelleme
        private DateTime lastProgressUpdate = DateTime.MinValue;
        private const int PROGRESS_UPDATE_INTERVAL_MS = 1000; // 1000ms = saniyede 1 güncelleme (4-5TB için optimize)

        public MainWindow()
        {
            InitializeComponent();
            
            // ThreadPool optimizasyonu - Büyük veri setleri için
            // Minimum worker thread sayısını artır (I/O bound işlemler için)
            int minWorkerThreads, minCompletionPortThreads;
            ThreadPool.GetMinThreads(out minWorkerThreads, out minCompletionPortThreads);
            int newMinWorkerThreads = Math.Max(minWorkerThreads, Environment.ProcessorCount * 8);
            int newMinCompletionPortThreads = Math.Max(minCompletionPortThreads, Environment.ProcessorCount * 8);
            ThreadPool.SetMinThreads(newMinWorkerThreads, newMinCompletionPortThreads);
            hashResults = new ObservableCollection<HashResult>();
            allHashResults = new ObservableCollection<HashResult>();
            compareResults = new ObservableCollection<HashResult>();
            allCompareResults = new ObservableCollection<HashResult>();
            fileCompareResults = new ObservableCollection<FileCompareRow>();
            comparisonDict = new Dictionary<string, HashResult>();
            dgResults.ItemsSource = hashResults;
            dgCompareResults.ItemsSource = compareResults;
            dgFileCompareResults.ItemsSource = fileCompareResults;
            
            // Dinamik dosya listesi başlat
            fileItems = new ObservableCollection<FileItem>();
            hashResultItems = new ObservableCollection<HashResultItem>();
            calculatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            fileListItemsControl.ItemsSource = fileItems;
            hashResultsItemsControl.ItemsSource = hashResultItems;
            
            // Toplu PDF butonu görünürlüğünü güncellemek için CollectionChanged event'ini dinle
            hashResultItems.CollectionChanged += (s, e) => UpdateTopluPdfButtonVisibility();
            
            // Başlangıçta buton görünürlüğünü kontrol et
            this.Loaded += (s, e) => UpdateTopluPdfButtonVisibility();
            
            // İlk dosyayı otomatik ekle (X butonu gizli - tek item olduğu için)
            AddNewFile();
            if (fileItems.Count > 0)
            {
                fileItems[0].CanRemove = false; // İlk item'ın X butonu gizli
            }
            
            // DataGrid kolonlarını güncelle - Başlangıçta seçili hash'lere göre
            this.Loaded += (s, e) => UpdateDataGridColumns();

            // Ayarları yükle - Kaydedilmiş ayarları hafızadan yükler
            LoadSettings();
            
            // Window SizeChanged event'ini dinle - Hash results için MaxWidth güncelle (debounce ile)
            System.Windows.Threading.DispatcherTimer windowResizeTimer = null;
            this.SizeChanged += (s, args) =>
            {
                if (hashResultsItemsControl != null && hashResultsItemsControl.Items.Count > 0)
                {
                    if (windowResizeTimer == null)
                    {
                        windowResizeTimer = new System.Windows.Threading.DispatcherTimer();
                        windowResizeTimer.Interval = TimeSpan.FromMilliseconds(300);
                        windowResizeTimer.Tick += (timerSender, timerArgs) =>
                        {
                            windowResizeTimer.Stop();
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                UpdateHashResultItemMaxWidths();
                            }), DispatcherPriority.Background);
                        };
                    }
                    windowResizeTimer.Stop();
                    windowResizeTimer.Start();
                }
            };
            
            // Debounce timer'ı başlat - Performans için
            resizeTimer = new System.Windows.Threading.DispatcherTimer();
            resizeTimer.Interval = TimeSpan.FromMilliseconds(500); // 500ms bekle (performans için artırıldı)
            resizeTimer.Tick += (s, args) =>
            {
                resizeTimer.Stop();
                // Sadece aktif tab için auto-size yap
                TabItem selectedTab = mainTabControl?.SelectedItem as TabItem;
                if (selectedTab != null)
                {
                    string tabHeader = selectedTab.Header?.ToString() ?? "";
                    if (tabHeader == "Klasör Hash Hesaplama" && dgResults != null && dgResults.IsLoaded)
                    {
                        AutoSizeDataGridColumns();
                    }
                }
            };
            
            // Karşılaştırma DataGrid için debounce timer
            compareResizeTimer = new System.Windows.Threading.DispatcherTimer();
            compareResizeTimer.Interval = TimeSpan.FromMilliseconds(500); // 500ms bekle
            compareResizeTimer.Tick += (s, args) =>
            {
                compareResizeTimer.Stop();
                // Sadece aktif tab için auto-size yap
                TabItem selectedTab = mainTabControl?.SelectedItem as TabItem;
                if (selectedTab != null)
                {
                    string tabHeader = selectedTab.Header?.ToString() ?? "";
                    if (tabHeader == "İki Klasör Karşılaştırma" && dgCompareResults != null && dgCompareResults.IsLoaded)
                    {
                        AutoSizeCompareDataGridColumns();
                    }
                }
            };
            
            // DataGrid kolonlarını güncelle - Ayarlar yüklendikten sonra
            this.Loaded += (s, e) => 
            {
                UpdateDataGridColumns();
                // İlk yüklemede gecikme ile ayarla (performans için)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (dgResults != null && dgResults.IsLoaded)
                    {
                        AutoSizeDataGridColumns();
                    }
                }), DispatcherPriority.Background);
            };
            
            // DataGrid yüklendiğinde header'ı sticky yap
            this.Loaded += (s, e) =>
            {
                if (dgResults != null)
                {
                    dgResults.Loaded += (sender, args) =>
                    {
                        MakeDataGridHeaderSticky();
                        AutoSizeDataGridColumns(); // İlk yüklemede boyutlandır
                    };
                    
                    // DataGrid boyutu değiştiğinde kolonları yeniden boyutlandır (responsive)
                    dgResults.SizeChanged += (sender, args) =>
                    {
                        if (dgResults.IsLoaded)
                        {
                            AutoSizeDataGridColumns();
                        }
                    };
                    
                    // Eğer zaten yüklenmişse hemen çağır
                    if (dgResults.IsLoaded)
                    {
                        MakeDataGridHeaderSticky();
                        AutoSizeDataGridColumns();
                    }
                }
            };
            
            // Pencere boyutu değiştiğinde kolonları otomatik ayarla (debounced) - Sadece başlangıçta
            // Kullanıcı kolonları manuel ayarladıktan sonra otomatik ayarlama devre dışı
            this.SizeChanged += (s, e) =>
            {
                // Otomatik ayarlamayı devre dışı bırak - Kullanıcı manuel ayarlayabilsin
                // if (e.WidthChanged)
                // {
                //     isResizing = true;
                //     resizeTimer.Stop();
                //     resizeTimer.Start(); // 300ms sonra ayarla
                // }
            };
            
            // Tab değiştiğinde footer'ı güncelle
            if (mainTabControl != null)
            {
                mainTabControl.SelectionChanged += MainTabControl_SelectionChanged;
            }
        }
        
        // Tab değiştiğinde footer'ı güncelle - Hangi tab aktifse ona göre footer bilgisini göster
        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (mainTabControl == null) return;
            
            TabItem selectedTab = mainTabControl.SelectedItem as TabItem;
            if (selectedTab == null) return;
            
            string tabHeader = selectedTab.Header?.ToString() ?? "";
            activeTabHeader = tabHeader; // Aktif tab'ı sakla
            
            // Önce tüm progress panel'leri gizle - Sadece aktif tab'da gösterilecek
            progressPanelFileHash.Visibility = Visibility.Collapsed;
            if (progressPanelCompare != null)
            {
                progressPanelCompare.Visibility = Visibility.Collapsed;
            }
            txtStatus.Visibility = Visibility.Collapsed;
            
            // Footer'ı temizle ve tab'a göre ayarla
            if (tabHeader == "Dosya Hash Hesaplama")
            {
                // Dosya hash hesaplama tab'ı - Sadece bu tab'ın footer'ı görünecek
                UpdateFileHashFooter();
                // Progress panel bu tab'da gösterilmez (dosya hash hesaplama için ayrı bir progress yok)
            }
            else if (tabHeader == "Klasör Hash Hesaplama")
            {
                // Klasör hash hesaplama tab'ı - Sadece bu tab'ın footer'ı görünecek
                UpdateFolderHashFooter();
                
                // Eğer işlem devam ediyorsa progress panel'i göster (sadece bu tab aktifken)
                if (isProcessingFolderHash)
                {
                    progressPanelFileHash.Visibility = Visibility.Visible;
                    txtFooter.Visibility = Visibility.Collapsed;
                }
                
                // DataGrid'i yenile - Sonuçların görünür olmasını sağla
                if (dgResults != null)
                {
                    dgResults.Items.Refresh();
                }
            }
            else if (tabHeader == "İki Dosya Karşılaştırma")
            {
                // İki dosya karşılaştırma tab'ı - Sadece bu tab'ın footer'ı görünecek
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = "Hazır";
                // Progress panel bu tab'da gösterilmez
            }
            else if (tabHeader == "İki Klasör Karşılaştırma")
            {
                // Karşılaştırma tab'ı - Sadece bu tab'ın footer'ı görünecek
                UpdateCompareHashFooter();
                
                // Eğer işlem devam ediyorsa progress panel'i göster (sadece bu tab aktifken)
                if (isProcessingCompare)
                {
                    progressPanelFileHash.Visibility = Visibility.Visible;
                    txtFooter.Visibility = Visibility.Collapsed;
                    if (progressPanelCompare != null)
                    {
                        progressPanelCompare.Visibility = Visibility.Visible;
                    }
                }
                
                // Status mesajını güncelle - Tab değiştiğinde anlık görünürlük için
                if (txtStatusCompare != null && !string.IsNullOrEmpty(txtStatusCompare.Text))
                {
                    // Mevcut status mesajını zorla güncelle
                    string currentText = txtStatusCompare.Text;
                    txtStatusCompare.Text = "";
                    txtStatusCompare.Text = currentText;
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
                
                // DataGrid'i yenile - Sonuçların görünür olmasını sağla
                if (dgCompareResults != null)
                {
                    dgCompareResults.Items.Refresh();
                }
            }
            else
            {
                // Diğer tab'lar için normal footer
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = "Hazır";
            }
        }
        
        // Dosya hash hesaplama footer'ını güncelle
        private void UpdateFileHashFooter()
        {
            if (hashResultItems == null) return;
            
            // Sadece aktif tab "Dosya Hash Hesaplama" ise göster
            if (activeTabHeader != "Dosya Hash Hesaplama")
            {
                return; // Başka tab aktifse footer'ı güncelleme
            }
            
            int totalCount = hashResultItems.Count;
            if (totalCount > 0)
            {
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = $"Toplam: {totalCount} dosya hesaplandı";
            }
            else
            {
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = "Hazır";
            }
            // Progress panel bu tab'da gösterilmez
            progressPanelFileHash.Visibility = Visibility.Collapsed;
            txtStatus.Visibility = Visibility.Collapsed;
        }
        
        // Klasör hash hesaplama footer'ını güncelle
        private void UpdateFolderHashFooter()
        {
            // Sadece aktif tab "Klasör Hash Hesaplama" ise göster
            if (activeTabHeader != "Klasör Hash Hesaplama")
            {
                return; // Başka tab aktifse footer'ı güncelleme
            }
            
            // hashResults kullan (DataGrid'de gösterilen ile aynı olmalı)
            // allHashResults sadece filtreleme için kullanılıyor
            var sourceResults = hashResults;
            
            if (sourceResults == null || sourceResults.Count == 0)
            {
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = "Hazır";
                if (!isProcessingFolderHash)
                {
                    progressPanelFileHash.Visibility = Visibility.Collapsed;
                }
                txtStatus.Visibility = Visibility.Collapsed;
                return;
            }
            
            int folderCount = sourceResults.Count(r => r.IsFolder);
            int fileCount = sourceResults.Count(r => !r.IsFolder);
            int totalCount = sourceResults.Count;
            
            if (totalCount > 0)
            {
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = $"Toplam: {totalCount} kayıt ({folderCount} klasör, {fileCount} dosya)";
            }
            else
            {
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = "Hazır";
            }
            // Progress panel sadece işlem devam ediyorsa ve bu tab aktifken gösterilir (MainTabControl_SelectionChanged'de kontrol edilir)
            if (!isProcessingFolderHash)
            {
                progressPanelFileHash.Visibility = Visibility.Collapsed;
            }
            txtStatus.Visibility = Visibility.Collapsed;
        }
        
        // Karşılaştırma hash footer'ını güncelle
        private void UpdateCompareHashFooter()
        {
            // Sadece aktif tab "İki Klasör Karşılaştırma" ise göster
            if (activeTabHeader != "İki Klasör Karşılaştırma")
            {
                return; // Başka tab aktifse footer'ı güncelleme
            }
            
            // compareResults kullan (DataGrid'de gösterilen ile aynı olmalı)
            // allCompareResults sadece filtreleme için kullanılıyor
            var sourceResults = compareResults;
            
            if (sourceResults == null || sourceResults.Count == 0)
            {
                txtFooter.Visibility = Visibility.Visible;
                txtFooter.Text = "Hazır";
                if (!isProcessingCompare)
                {
                    progressPanelFileHash.Visibility = Visibility.Collapsed;
                }
                txtStatus.Visibility = Visibility.Collapsed;
                return;
            }
            
            // Doğru hesaplama - DataGrid'de gösterilen sonuçlar için
            int folderCount = sourceResults.Count(r => r.IsFolder);
            int fileCount = sourceResults.Count(r => !r.IsFolder && !string.IsNullOrEmpty(r.FilePath));
            int totalCount = sourceResults.Count;
            
            txtFooter.Visibility = Visibility.Visible;
            txtFooter.Text = $"Toplam: {totalCount} kayıt ({folderCount} klasör, {fileCount} dosya)";
            
            // Progress panel sadece işlem devam ediyorsa ve bu tab aktifken gösterilir
            if (!isProcessingCompare)
            {
                progressPanelFileHash.Visibility = Visibility.Collapsed;
            }
            txtStatus.Visibility = Visibility.Collapsed;
        }

        // Ayarları yükle - Kaydedilmiş hash algoritması ayarlarını yükler
        private void LoadSettings()
        {
            try
            {
                useMD5 = Properties.Settings.Default.UseMD5;
                useSHA1 = Properties.Settings.Default.UseSHA1;
                
                // Varsayılan değerler - Eğer ayarlarda yoksa MD5 ve SHA1 aktif
                if (!Properties.Settings.Default.UseMD5 && !Properties.Settings.Default.UseSHA1)
                {
                    useMD5 = true;
                    useSHA1 = true;
                }
                
                pdfTitle = Properties.Settings.Default.PdfTitle ?? "Hash Sonuçları";
                pdfFileNumber = Properties.Settings.Default.PdfFileNumber ?? "";
                pdfOrganization = Properties.Settings.Default.PdfOrganization ?? "";
                pdfShowDateTime = Properties.Settings.Default.PdfShowDateTime;
            }
            catch
            {
                // Varsayılan değerler - MD5 ve SHA1 aktif
                useMD5 = true;
                useSHA1 = true;
                
                pdfTitle = "Hash Sonuçları";
                pdfFileNumber = "";
                pdfOrganization = "";
                pdfShowDateTime = true;
            }
        }

        // Ayarları kaydet - Hash algoritması ayarlarını hafızaya kaydeder
        private void SaveSettings()
        {
            Properties.Settings.Default.UseMD5 = useMD5;
            Properties.Settings.Default.UseSHA1 = useSHA1;
            
            Properties.Settings.Default.PdfTitle = pdfTitle;
            Properties.Settings.Default.PdfFileNumber = pdfFileNumber;
            Properties.Settings.Default.PdfOrganization = pdfOrganization;
            Properties.Settings.Default.PdfShowDateTime = pdfShowDateTime;
            
            Properties.Settings.Default.Save();
            
            // DataGrid kolonlarını güncelle - Seçili hash'lere göre kolonları göster/gizle
            UpdateDataGridColumns();
        }

        // DataGrid kolonlarını güncelle - Seçili hash algoritmalarına göre kolonları gösterir/gizler
        // DataGrid kolonlarını otomatik genişlik ayarla - Responsive ve içerik bazlı
        private void AutoSizeDataGridColumns()
        {
            if (dgResults == null || dgResults.Columns.Count == 0)
                return;
            
            // Eğer henüz yüklenmemişse, yükleme tamamlanana kadar bekle
            if (!dgResults.IsLoaded)
                return;
            
            // Sadece aktif tab için çalış (performans için)
            TabItem selectedTab = mainTabControl?.SelectedItem as TabItem;
            if (selectedTab != null)
            {
                string tabHeader = selectedTab.Header?.ToString() ?? "";
                if (tabHeader != "Klasör Hash Hesaplama")
                    return;
            }
            
            try
            {
                // DataGrid'in gerçek genişliğini al (scrollbar hariç)
                double availableWidth = dgResults.ActualWidth;
                if (availableWidth <= 0)
                {
                    return; // ActualWidth hazır değilse bekleme, tekrar çağrılacak
                }
                
                // Scrollbar genişliği için rezervasyon
                double scrollbarWidth = 0;
                var scrollViewer = FindVisualChild<ScrollViewer>(dgResults);
                if (scrollViewer != null && scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    scrollbarWidth = 20; // Scrollbar genişliği
                }
                availableWidth -= scrollbarWidth;
                
                // Sabit genişlikli kolonlar
                double fixedWidth = 0;
                fixedWidth += 60; // Sıra
                fixedWidth += 80; // Uzantı
                fixedWidth += 150; // Tarih
                
                // Kalan genişliği hesapla
                double remainingWidth = availableWidth - fixedWidth;
                if (remainingWidth <= 0)
                    return;
                
                // Otomatik genişlikli kolon sayısını hesapla
                int autoSizeColumns = 0;
                if (colMD5 != null && colMD5.Visibility == Visibility.Visible) autoSizeColumns++;
                if (colSHA1 != null && colSHA1.Visibility == Visibility.Visible) autoSizeColumns++;
                
                // Klasör Yolu, Dosya Yolu, Dosya Adı kolonları (her zaman görünür)
                autoSizeColumns += 3;
                
                if (autoSizeColumns == 0)
                    return;
                
                // İçerik bazlı genişlik hesaplama - Her kolon için ortalama içerik genişliği
                var columnWidths = new Dictionary<DataGridColumn, double>();
                double totalContentWidth = 0;
                
                // İlk 100 satırı kontrol et (performans için)
                int sampleCount = Math.Min(100, hashResults.Count);
                if (sampleCount > 0)
                {
                    // PixelsPerDip hesapla (DPI awareness için)
                    double pixelsPerDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    
                    foreach (var column in dgResults.Columns)
                    {
                        string header = column.Header?.ToString() ?? "";
                        double maxWidth = 0;
                        
                        for (int i = 0; i < sampleCount; i++)
                        {
                            var item = hashResults[i];
                            string content = "";
                            
                            if (header == "Klasör Yolu") content = item.FolderPath ?? "";
                            else if (header == "Dosya Yolu") content = item.FilePath ?? "";
                            else if (header == "Dosya Adı") content = item.FileName ?? "";
                            else if (column == colMD5) content = item.MD5Hash ?? "";
                            else if (column == colSHA1) content = item.SHA1Hash ?? "";
                            
                            if (!string.IsNullOrEmpty(content))
                            {
                                // Text wrapping için satır başına maksimum karakter sayısı
                                // pixelsPerDip zaten enclosing scope'ta tanımlı
                                var text = new System.Windows.Media.FormattedText(
                                    content.Length > 50 ? content.Substring(0, 50) + "..." : content,
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    System.Windows.FlowDirection.LeftToRight,
                                    new System.Windows.Media.Typeface("Arial"),
                                    13,
                                    System.Windows.Media.Brushes.Black,
                                    pixelsPerDip);
                                maxWidth = Math.Max(maxWidth, text.Width);
                            }
                        }
                        
                        // Minimum genişlik + padding
                        maxWidth = Math.Max(maxWidth, 100) + 20; // 20px padding
                        columnWidths[column] = maxWidth;
                        totalContentWidth += maxWidth;
                    }
                }
                
                // Eğer içerik bazlı genişlik hesaplanamadıysa, eşit dağıt
                if (totalContentWidth == 0 || columnWidths.Count == 0)
                {
                    double autoColumnWidth = remainingWidth / autoSizeColumns;
                    double minAutoWidth = 100;
                    if (autoColumnWidth < minAutoWidth)
                    {
                        autoColumnWidth = minAutoWidth;
                    }
                    
                    foreach (var column in dgResults.Columns)
                    {
                        string header = column.Header?.ToString() ?? "";
                        
                        if (header == "Sıra")
                        {
                            column.Width = 60;
                        }
                        else if (header == "Uzantı")
                        {
                            column.Width = 80;
                        }
                        else if (header == "Tarih")
                        {
                            column.Width = 150;
                        }
                        else if (header == "Klasör Yolu" || header == "Dosya Yolu" || header == "Dosya Adı")
                        {
                            column.Width = Math.Max(autoColumnWidth, column.MinWidth);
                        }
                        else if (column == colMD5 || column == colSHA1)
                        {
                            if (column.Visibility == Visibility.Visible)
                            {
                                double hashWidth = autoColumnWidth * 1.2; // %20 daha geniş
                                column.Width = Math.Max(hashWidth, column.MinWidth);
                            }
                        }
                    }
                }
                else
                {
                    // İçerik bazlı genişlik dağıtımı
                    double scaleFactor = remainingWidth / totalContentWidth;
                    
                    foreach (var column in dgResults.Columns)
                    {
                        string header = column.Header?.ToString() ?? "";
                        
                        if (header == "Sıra")
                        {
                            column.Width = 60;
                        }
                        else if (header == "Uzantı")
                        {
                            column.Width = 80;
                        }
                        else if (header == "Tarih")
                        {
                            column.Width = 150;
                        }
                        else if (columnWidths.ContainsKey(column))
                        {
                            double calculatedWidth = columnWidths[column] * scaleFactor;
                            column.Width = Math.Max(calculatedWidth, column.MinWidth);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda sessizce devam et
                System.Diagnostics.Debug.WriteLine($"AutoSizeDataGridColumns error: {ex.Message}");
            }
        }
        
        private void UpdateDataGridColumns()
        {
            // Klasör hash hesaplama sekmesi
            if (colMD5 != null) colMD5.Visibility = useMD5 ? Visibility.Visible : Visibility.Collapsed;
            if (colSHA1 != null) colSHA1.Visibility = useSHA1 ? Visibility.Visible : Visibility.Collapsed;
            // SHA256, SHA384, SHA512 her zaman gizli
            if (colSHA256 != null) colSHA256.Visibility = Visibility.Collapsed;
            if (colSHA384 != null) colSHA384.Visibility = Visibility.Collapsed;
            if (colSHA512 != null) colSHA512.Visibility = Visibility.Collapsed;
            
            // Karşılaştırma sekmesi
            if (colCompareMD5 != null) colCompareMD5.Visibility = useMD5 ? Visibility.Visible : Visibility.Collapsed;
            if (colCompareSHA1 != null) colCompareSHA1.Visibility = useSHA1 ? Visibility.Visible : Visibility.Collapsed;
            // SHA256, SHA384, SHA512 her zaman gizli
            if (colCompareSHA256 != null) colCompareSHA256.Visibility = Visibility.Collapsed;
            if (colCompareSHA384 != null) colCompareSHA384.Visibility = Visibility.Collapsed;
            if (colCompareSHA512 != null) colCompareSHA512.Visibility = Visibility.Collapsed;
            
            // Kolon genişliklerini otomatik ayarla (debounced)
            if (resizeTimer != null)
            {
                resizeTimer.Stop();
                resizeTimer.Start();
            }
        }

        // Ayarlar penceresini aç - Kullanıcı hangi hash algoritmalarının hesaplanacağını seçer
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow settingsWindow = new SettingsWindow(useMD5, useSHA1,
                pdfTitle, pdfFileNumber, pdfOrganization, pdfShowDateTime);
            settingsWindow.Owner = this;
            
            if (settingsWindow.ShowDialog() == true)
            {
                // Eski ayarları sakla (değişiklik kontrolü için)
                bool oldUseMD5 = useMD5;
                bool oldUseSHA1 = useSHA1;
                bool oldPdfShowDateTime = pdfShowDateTime;
                
                // Yeni ayarları al
                useMD5 = settingsWindow.UseMD5;
                useSHA1 = settingsWindow.UseSHA1;
                pdfTitle = settingsWindow.PdfTitle;
                pdfFileNumber = settingsWindow.PdfFileNumber;
                pdfOrganization = settingsWindow.PdfOrganization;
                pdfShowDateTime = settingsWindow.PdfShowDateTime;
                
                // Ayarlar değiştiyse mevcut hash sonuçlarını güncelle
                if (oldUseMD5 != useMD5 || oldUseSHA1 != useSHA1 || oldPdfShowDateTime != pdfShowDateTime)
                {
                    UpdateAllHashResults();
                }
                
                // Ayarları hafızaya kaydet
                SaveSettings();
            }
        }

        // Yeni dosya ekleme - Kullanıcı yeni bir dosya alanı ekler
        private void BtnAddFile_Click(object sender, RoutedEventArgs e)
        {
            // Dosya seçme dialogu aç
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Multiselect = true; // Birden fazla dosya seçilebilir
            
            if (openFileDialog.ShowDialog() == true)
            {
                foreach (string selectedPath in openFileDialog.FileNames)
                {
                    // Dosya yolu normalize et
                    string normalizedPath = Path.GetFullPath(selectedPath.Trim());
                    
                    // Aynı dosya var mı kontrol et
                    var existingFile = fileItems.FirstOrDefault(f => 
                        !string.IsNullOrEmpty(f.FilePath) && 
                        f.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
                    
                    if (existingFile != null)
                    {
                        // Aynı dosya zaten var - mevcut dosyayı üste taşı
                        fileItems.Remove(existingFile);
                        fileItems.Insert(0, existingFile);
                        existingFile.IsDuplicate = true;
                        existingFile.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(230, 245, 255));
                        continue;
                    }
                    
                    // Boş bir dosya item'ı var mı kontrol et
                    var emptyFileItem = fileItems.FirstOrDefault(f => string.IsNullOrEmpty(f.FilePath));
                    
                    if (emptyFileItem != null)
                    {
                        // Boş item'a dosyayı ekle
                        emptyFileItem.FilePath = normalizedPath;
                        emptyFileItem.IsDuplicate = false;
                        emptyFileItem.BackgroundColor = System.Windows.Media.Brushes.Transparent;
                        emptyFileItem.CanRemove = true; // Dosya eklendi, X butonu görünür
                    }
                    else
                    {
                        // Yeni dosya ekle
                        int fileNumber = fileItems.Count + 1;
                        var newFileItem = new FileItem
                        {
                            Label = $"Dosya {fileNumber}:",
                            FilePath = normalizedPath,
                            IsDuplicate = false,
                            BackgroundColor = System.Windows.Media.Brushes.Transparent,
                            CanRemove = true // Dosya eklendi, X butonu görünür
                        };
                        fileItems.Add(newFileItem);
                    }
                }
                
                // Dosya sayısını kontrol et - Eğer 1 tane kaldıysa X butonunu gizle
                if (fileItems.Count == 1 && string.IsNullOrEmpty(fileItems[0].FilePath))
                {
                    fileItems[0].CanRemove = false; // Tek boş item kaldı, X butonu gizli
                }
                
                // Dosya sayısını kontrol et - Eğer 1 tane kaldıysa X butonunu gizle
                if (fileItems.Count == 1 && string.IsNullOrEmpty(fileItems[0].FilePath))
                {
                    fileItems[0].CanRemove = false; // Tek boş item kaldı, X butonu gizli
                }
                
                // Etiketleri güncelle
                UpdateFileLabels();
                
                // MaxWidth'i güncelle
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateFileItemMaxWidths();
                }), DispatcherPriority.Loaded);
            }
        }
        
        // Yeni dosya ekleme fonksiyonu - Boş dosya item'ı ekler
        private void AddNewFile()
        {
            int fileNumber = fileItems.Count + 1;
            bool canRemove = fileItems.Count > 0; // Eğer zaten item varsa, X butonu görünür
            fileItems.Add(new FileItem
            {
                Label = $"Dosya {fileNumber}:",
                FilePath = "",
                CanRemove = canRemove
            });
            
            // Eğer şimdi 1 tane kaldıysa, X butonunu gizle
            if (fileItems.Count == 1)
            {
                fileItems[0].CanRemove = false;
            }
            
            // MaxWidth'i güncelle
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateFileItemMaxWidths();
            }), DispatcherPriority.Loaded);
        }
        // Sürükle-bırak: Dosya sürüklenirken
        private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
        {
            // Sadece dosyalar kabul edilir
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
        }
        
        // Sürükle-bırak: Dosya bırakıldığında
        private void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                
                foreach (string filePath in files)
                {
                    // Sadece dosyalar kabul edilir (klasörler değil)
                    if (File.Exists(filePath))
                    {
                        // Aynı dosya var mı kontrol et
                        var existingFile = fileItems.FirstOrDefault(f => 
                            !string.IsNullOrEmpty(f.FilePath) && 
                            f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                        
                        if (existingFile != null)
                        {
                            // Aynı dosya zaten var - mevcut dosyayı üste taşı ve vurgula
                            fileItems.Remove(existingFile);
                            fileItems.Insert(0, existingFile);
                            existingFile.IsDuplicate = true;
                            existingFile.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(230, 245, 255)); // Açık mavi - göz yormayan
                            
                            // Etiketleri güncelle
                            UpdateFileLabels();
                            
                            // ScrollViewer'ı en üste kaydır ve item'a focus yap
                            ScrollToItem(existingFile);
                            
                            continue; // Bu dosyayı atla, zaten var
                        }
                        
                        // Boş bir dosya item'ı var mı kontrol et (FilePath boş olan)
                        var emptyFileItem = fileItems.FirstOrDefault(f => string.IsNullOrEmpty(f.FilePath));
                        
                        if (emptyFileItem != null)
                        {
                            // Boş item'a dosyayı ekle (normalize et)
                            emptyFileItem.FilePath = Path.GetFullPath(filePath.Trim());
                            emptyFileItem.IsDuplicate = false;
                            emptyFileItem.BackgroundColor = System.Windows.Media.Brushes.Transparent;
                            emptyFileItem.CanRemove = true; // Dosya eklendi, X butonu görünür
                            
                            // Eğer bu dosya başka bir yerde de varsa, tümünü işaretle
                            var duplicates = fileItems.Where(f => 
                                !string.IsNullOrEmpty(f.FilePath) && 
                                f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList();
                            
                            if (duplicates.Count > 1)
                            {
                                foreach (var dup in duplicates)
                                {
                                    dup.IsDuplicate = true;
                                    dup.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(230, 245, 255)); // Açık mavi - göz yormayan
                                }
                                
                                // İlk duplicate'e scroll yap
                                ScrollToItem(duplicates[0]);
                            }
                        }
                        else
                        {
                            // Yeni dosya ekle (boş item yoksa) - normalize et
                            int fileNumber = fileItems.Count + 1;
                            var newFileItem = new FileItem
                            {
                                Label = $"Dosya {fileNumber}:",
                                FilePath = Path.GetFullPath(filePath.Trim()),
                                IsDuplicate = false,
                                BackgroundColor = System.Windows.Media.Brushes.Transparent,
                                CanRemove = true // Dosya eklendi, X butonu görünür
                            };
                            
                            fileItems.Add(newFileItem);
                            
                            // Eğer bu dosya başka bir yerde de varsa, tümünü işaretle
                            var duplicates = fileItems.Where(f => 
                                !string.IsNullOrEmpty(f.FilePath) && 
                                f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList();
                            
                            if (duplicates.Count > 1)
                            {
                                foreach (var dup in duplicates)
                                {
                                    dup.IsDuplicate = true;
                                    dup.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(230, 245, 255)); // Açık mavi - göz yormayan
                                }
                                
                                // İlk duplicate'e scroll yap
                                ScrollToItem(duplicates[0]);
                            }
                        }
                    }
                }
                
                // Etiketleri güncelle
                UpdateFileLabels();
                
                // MaxWidth'i güncelle
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateFileItemMaxWidths();
                }), DispatcherPriority.Loaded);
            }
        }
        
        // Dosya seçme - Dinamik dosya seçimi
        private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Button btn = sender as System.Windows.Controls.Button;
            if (btn != null && btn.Tag is FileItem fileItem)
            {
                Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
                if (openFileDialog.ShowDialog() == true)
                {
                    string selectedPath = openFileDialog.FileName;
                    
                    // Aynı dosya var mı kontrol et
                    var existingFile = fileItems.FirstOrDefault(f => 
                        !string.IsNullOrEmpty(f.FilePath) && 
                        f.FilePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
                    
                    if (existingFile != null && existingFile != fileItem)
                    {
                        // Aynı dosya zaten var - mevcut dosyayı üste taşı ve vurgula
                        fileItems.Remove(existingFile);
                        fileItems.Insert(0, existingFile);
                        existingFile.IsDuplicate = true;
                        existingFile.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(230, 245, 255)); // Açık mavi - göz yormayan
                        
                        // Etiketleri güncelle
                        UpdateFileLabels();
                        
                        // ScrollViewer'ı en üste kaydır ve item'a focus yap
                        ScrollToItem(existingFile);
                        
                        System.Windows.MessageBox.Show("Bu dosya zaten listede! En üste taşındı.", "Bilgi", 
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    
                    // Yeni dosya ekle (normalize et)
                    fileItem.FilePath = Path.GetFullPath(selectedPath.Trim());
                    fileItem.IsDuplicate = false;
                    fileItem.BackgroundColor = System.Windows.Media.Brushes.Transparent;
                    
                    // Eğer bu dosya başka bir yerde de varsa, tümünü işaretle
                    var duplicates = fileItems.Where(f => 
                        !string.IsNullOrEmpty(f.FilePath) && 
                        f.FilePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)).ToList();
                    
                    if (duplicates.Count > 1)
                    {
                        foreach (var dup in duplicates)
                        {
                            dup.IsDuplicate = true;
                            dup.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(230, 245, 255)); // Açık mavi - göz yormayan
                        }
                        
                        // İlk duplicate'e scroll yap
                        ScrollToItem(duplicates[0]);
                    }
                    
                    // MaxWidth'i güncelle
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateFileItemMaxWidths();
                    }), DispatcherPriority.Loaded);
                }
            }
        }
        
        // Belirli bir item'a scroll yap
        private void ScrollToItem(FileItem item)
        {
            // ItemsControl'ün parent'ını bul (ScrollViewer)
            var scrollViewer = FindVisualChild<ScrollViewer>(fileListItemsControl);
            if (scrollViewer != null)
            {
                // Önce en üste kaydır
                scrollViewer.ScrollToTop();
                
                // Sonra item'ı bul ve ona scroll yap
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var container = fileListItemsControl.ItemContainerGenerator.ContainerFromItem(item);
                    if (container != null)
                    {
                        // FrameworkElement'e cast et ve BringIntoView çağır
                        var frameworkElement = container as FrameworkElement;
                        if (frameworkElement != null)
                        {
                            frameworkElement.BringIntoView();
                            
                            // Kısa bir animasyon efekti için renk değişimi
                            var originalColor = item.BackgroundColor;
                            item.BackgroundColor = new System.Windows.Media.SolidColorBrush(
                                System.Windows.Media.Color.FromRgb(200, 235, 255)); // Biraz daha koyu mavi
                            
                            // 3 saniye sonra orijinal renge dön (daha uzun süre görünür)
                            Task.Delay(3000).ContinueWith(t =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    item.BackgroundColor = System.Windows.Media.Brushes.Transparent;
                                });
                            });
                        }
                    }
                }), DispatcherPriority.Loaded);
            }
        }
        
        // Visual tree'de child bulma helper metodu
        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T)
                    return (T)child;
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }
        
        // Dosya listesi yüklendiğinde her item için MaxWidth ayarla
        private void FileListItemsControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (fileListItemsControl == null) return;
            
            // Her item için MaxWidth ayarla (performans için gecikme ile)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateFileItemMaxWidths();
            }), DispatcherPriority.Background);
            
            // ItemsControl'in SizeChanged event'ini dinle (debounce ile)
            System.Windows.Threading.DispatcherTimer fileListResizeTimer = null;
            fileListItemsControl.SizeChanged += (s, args) =>
            {
                if (fileListResizeTimer == null)
                {
                    fileListResizeTimer = new System.Windows.Threading.DispatcherTimer();
                    fileListResizeTimer.Interval = TimeSpan.FromMilliseconds(300);
                    fileListResizeTimer.Tick += (timerSender, timerArgs) =>
                    {
                        fileListResizeTimer.Stop();
                        UpdateFileItemMaxWidths();
                    };
                }
                fileListResizeTimer.Stop();
                fileListResizeTimer.Start();
            };
            
            // Yeni item eklendiğinde de güncelle (performans için gecikme ile)
            fileListItemsControl.ItemContainerGenerator.StatusChanged += (s, args) =>
            {
                if (fileListItemsControl.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateFileItemMaxWidths();
                    }), DispatcherPriority.Background);
                }
            };
        }
        
        // Dosya item'ları için MaxWidth güncelle
        private void UpdateFileItemMaxWidths()
        {
            if (fileListItemsControl == null) return;
            
            try
            {
                // ScrollViewer'ı bul (parent Border içinde)
                var parentBorder = fileListItemsControl.Parent as FrameworkElement;
                var scrollViewer = parentBorder?.Parent as ScrollViewer;
                double availableWidth = scrollViewer != null && scrollViewer.ActualWidth > 0 
                    ? scrollViewer.ActualWidth 
                    : (fileListItemsControl.ActualWidth > 0 ? fileListItemsControl.ActualWidth : 800);
                
                if (availableWidth <= 0) return;
                
                // Her item container'ı bul
                foreach (var item in fileListItemsControl.Items)
                {
                    var container = fileListItemsControl.ItemContainerGenerator.ContainerFromItem(item);
                    if (container != null)
                    {
                        // Border'ı bul
                        var border = FindVisualChild<Border>(container);
                        if (border != null)
                        {
                            // Grid'i bul
                            var grid = FindVisualChild<Grid>(border);
                            if (grid != null)
                            {
                                // TextBlock'ları bul
                                var textBlocks = FindVisualChildren<TextBlock>(grid).ToList();
                                foreach (var textBlock in textBlocks)
                                {
                                    // Dosya yolu TextBlock'u (Label değil, uzun metin içeren)
                                    if (textBlock.Text != null && textBlock.Text.Length > 20 && 
                                        (textBlock.Text.Contains("\\") || textBlock.Text.Contains("/") || textBlock.Text.Contains(".")))
                                    {
                                        // ScrollViewer genişliğinden, Label (90+12), butonlar (90+40+8+8), padding (12*2+10*2) çıkar
                                        double maxWidth = availableWidth - 90 - 12 - 90 - 40 - 8 - 8 - 12 - 12 - 10 - 10 - 20;
                                        if (maxWidth > 150)
                                        {
                                            textBlock.MaxWidth = maxWidth;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }
        
        // Visual tree'de tüm child'ları bulma helper metodu
        private IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T)
                    yield return (T)child;
                foreach (var childOfChild in FindVisualChildren<T>(child))
                    yield return childOfChild;
            }
        }
        
        // Hash sonuçları listesi yüklendiğinde her item için MaxWidth ayarla
        private void HashResultsItemsControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (hashResultsItemsControl == null) return;
            
            // Her item için MaxWidth ayarla (performans için gecikme ile)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateHashResultItemMaxWidths();
            }), DispatcherPriority.Background);
            
            // ItemsControl'in SizeChanged event'ini dinle (debounce ile)
            System.Windows.Threading.DispatcherTimer hashResultsResizeTimer = null;
            hashResultsItemsControl.SizeChanged += (s, args) =>
            {
                if (hashResultsResizeTimer == null)
                {
                    hashResultsResizeTimer = new System.Windows.Threading.DispatcherTimer();
                    hashResultsResizeTimer.Interval = TimeSpan.FromMilliseconds(300);
                    hashResultsResizeTimer.Tick += (timerSender, timerArgs) =>
                    {
                        hashResultsResizeTimer.Stop();
                        UpdateHashResultItemMaxWidths();
                    };
                }
                hashResultsResizeTimer.Stop();
                hashResultsResizeTimer.Start();
            };
            
            // ScrollViewer'ın SizeChanged event'ini de dinle (debounce ile)
            var parentBorder = hashResultsItemsControl.Parent as FrameworkElement;
            var scrollViewer = parentBorder?.Parent as ScrollViewer;
            if (scrollViewer != null)
            {
                System.Windows.Threading.DispatcherTimer scrollViewerResizeTimer = null;
                scrollViewer.SizeChanged += (s, args) =>
                {
                    if (scrollViewerResizeTimer == null)
                    {
                        scrollViewerResizeTimer = new System.Windows.Threading.DispatcherTimer();
                        scrollViewerResizeTimer.Interval = TimeSpan.FromMilliseconds(300);
                        scrollViewerResizeTimer.Tick += (timerSender, timerArgs) =>
                        {
                            scrollViewerResizeTimer.Stop();
                            UpdateHashResultItemMaxWidths();
                        };
                    }
                    scrollViewerResizeTimer.Stop();
                    scrollViewerResizeTimer.Start();
                };
            }
            
            // Yeni item eklendiğinde de güncelle (performans için gecikme ile)
            hashResultsItemsControl.ItemContainerGenerator.StatusChanged += (s, args) =>
            {
                if (hashResultsItemsControl.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateHashResultItemMaxWidths();
                    }), DispatcherPriority.Background);
                }
            };
        }
        
        // Hash sonuç item'ları için MaxWidth güncelle - UpdateFileItemMaxWidths ile aynı mantık
        private void UpdateHashResultItemMaxWidths()
        {
            if (hashResultsItemsControl == null) return;
            
            try
            {
                // ScrollViewer'ı bul (parent Border içinde) - UpdateFileItemMaxWidths ile aynı mantık
                var parentBorder = hashResultsItemsControl.Parent as FrameworkElement;
                var scrollViewer = parentBorder?.Parent as ScrollViewer;
                double availableWidth = scrollViewer != null && scrollViewer.ActualWidth > 0 
                    ? scrollViewer.ActualWidth 
                    : (hashResultsItemsControl.ActualWidth > 0 ? hashResultsItemsControl.ActualWidth : 800);
                
                if (availableWidth <= 0) return;
                
                // Her item container'ı bul
                foreach (var item in hashResultsItemsControl.Items)
                {
                    var container = hashResultsItemsControl.ItemContainerGenerator.ContainerFromItem(item);
                    if (container != null)
                    {
                        // Border'ı bul
                        var border = FindVisualChild<Border>(container);
                        if (border != null)
                        {
                            // Grid'i bul
                            var grid = FindVisualChild<Grid>(border);
                            if (grid != null)
                            {
                                // TextBlock'ları bul (dosya adı)
                                var textBlocks = FindVisualChildren<TextBlock>(grid).ToList();
                                foreach (var textBlock in textBlocks)
                                {
                                    // Dosya adı TextBlock'u (FontWeight="Bold" olan ve FontSize="14")
                                    if (textBlock.FontWeight == FontWeights.Bold && textBlock.FontSize == 14 && textBlock.Text != null)
                                    {
                                        // ScrollViewer genişliğinden, butonlar (75+35+5+8), padding (10*2+15*2), margin (10) çıkar
                                        // Butonlar: 75 (PDF) + 35 (X) + 5 (PDF margin) + 8 (X margin) = 123
                                        // Border padding: 10*2 = 20
                                        // Dış Border padding: 15*2 = 30
                                        // TextBlock margin: 10 (sağ)
                                        // Rezerv: 20
                                        double maxWidth = availableWidth - 123 - 20 - 30 - 10 - 20;
                                        if (maxWidth > 150)
                                        {
                                            textBlock.MaxWidth = maxWidth;
                                        }
                                        break; // İlk eşleşen TextBlock'u bulduk, diğerlerine bakmaya gerek yok
                                    }
                                }
                                
                                // TextBox'ları bul (hash sonuçları)
                                var textBoxes = FindVisualChildren<System.Windows.Controls.TextBox>(grid).ToList();
                                foreach (var textBox in textBoxes)
                                {
                                    // Hash sonuçları TextBox'u (IsReadOnly="True" olan)
                                    if (textBox.IsReadOnly && textBox.TextWrapping == System.Windows.TextWrapping.Wrap)
                                    {
                                        // ScrollViewer genişliğinden, padding (10*2+15*2), TextBox padding (8*2) çıkar
                                        // Border padding: 10*2 = 20
                                        // Dış Border padding: 15*2 = 30
                                        // TextBox padding: 8*2 = 16
                                        // Rezerv: 20
                                        double maxWidth = availableWidth - 20 - 30 - 16 - 20;
                                        if (maxWidth > 150)
                                        {
                                            textBox.MaxWidth = maxWidth;
                                        }
                                        break; // İlk eşleşen TextBox'u bulduk, diğerlerine bakmaya gerek yok
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateHashResultItemMaxWidths error: {ex.Message}");
            }
        }
        
        // Dosya kaldırma - Seçilen dosyayı listeden kaldırır
        private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Button btn = sender as System.Windows.Controls.Button;
            if (btn != null && btn.Tag is FileItem fileItem)
            {
                // Eğer bu dosya hesaplanmışsa, calculatedFiles'den de kaldır (tekrar hesaplanabilir hale getir)
                if (!string.IsNullOrEmpty(fileItem.FilePath))
                {
                    string normalizedPath = Path.GetFullPath(fileItem.FilePath.Trim());
                    if (calculatedFiles.Contains(normalizedPath))
                    {
                        calculatedFiles.Remove(normalizedPath);
                    }
                }
                
                fileItems.Remove(fileItem);
                
                // Dosya sayısını kontrol et - Eğer 1 tane kaldıysa X butonunu gizle
                var remainingItems = fileItems.ToList();
                if (remainingItems.Count == 1)
                {
                    remainingItems[0].CanRemove = false; // Tek item kaldı, X butonu gizli
                }
                else if (remainingItems.Count > 1)
                {
                    // Birden fazla item varsa, tüm boş item'ların X butonu görünür olmalı
                    foreach (var item in remainingItems.Where(f => string.IsNullOrEmpty(f.FilePath)))
                    {
                        item.CanRemove = true;
                    }
                }
                
                // Etiketleri yeniden numaralandır
                UpdateFileLabels();
            }
        }
        
        // Dosya etiketlerini güncelle - Dosya kaldırıldıktan sonra numaralandırmayı düzeltir
        private void UpdateFileLabels()
        {
            for (int i = 0; i < fileItems.Count; i++)
            {
                fileItems[i].Label = $"Dosya {i + 1}:";
            }
        }

        // Dosya hash hesaplama - Seçilen dosyaların hash değerlerini hesaplar (sadece yeni dosyalar)
        private async void BtnCalculateFileHash_Click(object sender, RoutedEventArgs e)
        {
            // İptal flag'ini sıfırla
            isCancellationRequested = false;
            
            // İptal token'ı oluştur
            cancellationTokenSource = new System.Threading.CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            
            try
            {
                // Seçili dosyaları filtrele ve dosya yollarını normalize et
                var selectedFilesWithPaths = fileItems
                    .Where(f => !string.IsNullOrEmpty(f.FilePath) && File.Exists(f.FilePath))
                    .Select(f => 
                    {
                        try
                        {
                            string normalizedPath = Path.GetFullPath(f.FilePath.Trim());
                            return new { 
                                FileItem = f, 
                                NormalizedPath = normalizedPath,
                                IsValid = true
                            };
                        }
                        catch
                        {
                            return new { 
                                FileItem = f, 
                                NormalizedPath = f.FilePath.Trim(),
                                IsValid = false
                            };
                        }
                    })
                    .Where(f => f.IsValid && !string.IsNullOrEmpty(f.NormalizedPath))
                    .ToList();
                
                if (selectedFilesWithPaths.Count == 0)
                {
                    System.Windows.MessageBox.Show("Lütfen en az bir dosya seçin.", "Uyarı", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Sadece henüz hesaplanmamış dosyaları filtrele
                var newFilesWithPaths = selectedFilesWithPaths
                    .Where(f => !calculatedFiles.Contains(f.NormalizedPath))
                    .ToList();
                
                // Daha önce hesaplanmış dosyaları bul
                var alreadyCalculatedFiles = selectedFilesWithPaths
                    .Where(f => calculatedFiles.Contains(f.NormalizedPath))
                    .Select(f => Path.GetFileName(f.NormalizedPath))
                    .ToList();
                
                if (newFilesWithPaths.Count == 0)
                {
                    // Tüm dosyalar zaten hesaplanmış
                    string message = "Tüm seçili dosyalar daha önce hesaplanmış.";
                    if (alreadyCalculatedFiles.Count > 0 && alreadyCalculatedFiles.Count <= 5)
                    {
                        message += $"\n\nDaha önce hesaplanmış dosyalar:\n{string.Join("\n", alreadyCalculatedFiles)}";
                    }
                    else if (alreadyCalculatedFiles.Count > 5)
                    {
                        message += $"\n\n{alreadyCalculatedFiles.Count} dosya daha önce hesaplanmış.";
                    }
                    
                    System.Windows.MessageBox.Show(message, "Bilgi", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                // FileItem listesini oluştur
                var newFiles = newFilesWithPaths.Select(f => f.FileItem).ToList();
                
                // Overlay mesajını hazırla
                string overlayMessage = "";
                if (alreadyCalculatedFiles.Count > 0)
                {
                    overlayMessage = $"⚠️ {alreadyCalculatedFiles.Count} dosya daha önce hesaplanmış ve atlanacak.\n";
                    if (alreadyCalculatedFiles.Count <= 3)
                    {
                        overlayMessage += $"Atlanan: {string.Join(", ", alreadyCalculatedFiles)}\n\n";
                    }
                    overlayMessage += $"✅ {newFilesWithPaths.Count} yeni dosya hesaplanıyor...";
                }
                else
                {
                    overlayMessage = $"Toplam {newFiles.Count} dosya bulundu. Hash hesaplanıyor...";
                }

                // Progress bar'ı başlat ve footer'ı renklendir - Sadece aktif tab'da göster
                await Dispatcher.InvokeAsync(() =>
                {
                    // Overlay'i göster - Önceki hesaplamadan kalan overlay'i temizle ve yeniden aç
                    if (overlayBorder != null)
                    {
                        // Önce overlay'i zorla kapat, sonra aç (temiz başlangıç için)
                        overlayBorder.Visibility = Visibility.Collapsed;
                        overlayBorder.Visibility = Visibility.Visible;
                        
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "İşlem Yapılıyor...";
                        }
                        if (overlayProgressText != null)
                        {
                            overlayProgressText.Text = overlayMessage;
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Visible;
                            overlayProgressBar.IsIndeterminate = true;
                            overlayProgressBar.Value = 0; // Reset progress bar
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Collapsed;
                        }
                        // İptal butonunu göster
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Visible;
                        }
                    }
                    
                    // Butonu disable et - Hesaplama başladığında
                    if (btnCalculateFileHash != null)
                    {
                        btnCalculateFileHash.IsEnabled = false;
                    }
                    
                    // Sadece "Dosya Hash Hesaplama" tab'ı aktifse progress panel'i göster
                    if (activeTabHeader == "Dosya Hash Hesaplama")
                    {
                        progressPanelFileHash.Visibility = Visibility.Visible;
                        txtFooter.Visibility = Visibility.Collapsed;
                    }
                    progressBarFileHash.Maximum = 100;
                    progressBarFileHash.Value = 0;
                    progressBarFileHash.IsIndeterminate = false;
                    btnCalculateFileHash.IsEnabled = false;
                    txtProgressFileHash.Text = "Hazırlanıyor...";
                    // Footer'ı renklendir
                    // Footer'ı yeşil tonunda renklendir (dosya hesaplamadaki gibi)
                    footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(240, 255, 240)); // Açık yeşil // Açık mavi
                });

                // Yeni dosyalar için hash hesapla
                int totalFiles = newFiles.Count;
                int currentIndex = 0;
                
                // Normalize edilmiş yolları sakla (calculatedFiles'e eklemek için)
                var filePathsMap = newFilesWithPaths.ToDictionary(f => f.FileItem, f => f.NormalizedPath);
                
                foreach (var fileItem in newFiles)
                {
                    currentIndex++;
                    // Normalize edilmiş yolu kullan (daha önce hesaplanmış)
                    string filePath = filePathsMap[fileItem];
                    string fileName = Path.GetFileName(filePath);
                    HashInfo hashInfo = null;
                    
                    // Dosya boyutunu al (progress hesaplama için)
                    long fileSize = 0;
                    try
                    {
                        FileInfo fileInfo = new FileInfo(filePath);
                        fileSize = fileInfo.Length;
                    }
                    catch { }
                    
                    // Progress güncelle - başlangıç - Sadece aktif tab'da göster
                    double baseProgressPercent = ((currentIndex - 1.0) / totalFiles) * 100.0;
                    double fileProgressRange = 100.0 / totalFiles; // Her dosya için progress aralığı
                    
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Sadece "Dosya Hash Hesaplama" tab'ı aktifse progress panel'i göster
                        if (activeTabHeader == "Dosya Hash Hesaplama")
                        {
                            progressPanelFileHash.Visibility = Visibility.Visible;
                            txtFooter.Visibility = Visibility.Collapsed;
                            progressBarFileHash.Value = baseProgressPercent;
                            txtProgressFileHash.Text = $"{currentIndex}/{totalFiles} - {fileName} başlatılıyor...";
                        }
                    });
                    
                    // Hash hesaplama - gerçek progress ile
                    double currentFileProgress = baseProgressPercent;
                    
                    // İptal kontrolü
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break; // Döngüden çık
                    }
                    
                    await Task.Run(() =>
                    {
                        // İptal kontrolü - Task başlamadan önce kontrol et
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        
                        hashInfo = CalculateFileHashWithProgress(filePath, fileSize, cancellationToken, (progress) =>
                        {
                            // İptal kontrolü - UI güncellemesinden önce kontrol et
                            if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                            {
                                return; // UI güncellemesini atla
                            }
                            
                            // Progress callback'inde de iptal kontrolü yap
                            if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                            {
                                return; // UI güncellemesini atla
                            }
                            
                            // Gerçek dosya okuma progress'i (0-1 arası)
                            double fileProgressPercent = progress * 100.0; // 0-100 arası
                            double totalProgress = baseProgressPercent + (fileProgressRange * fileProgressPercent / 100.0);
                            currentFileProgress = totalProgress;
                            
                            // UI güncellemesi - Sadece aktif tab'da göster
                            // İptal edilmişse UI güncellemesini yapma
                            if (!cancellationToken.IsCancellationRequested && !isCancellationRequested)
                            {
                                // İptal kontrolü - UI güncellemesinden önce kontrol et
                                if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                                {
                                    return; // UI güncellemesini atla
                                }
                                
                                Dispatcher.InvokeAsync(() =>
                                {
                                    // İptal kontrolü - UI thread'inde de kontrol et
                                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                                    {
                                        return;
                                    }
                                    
                                    // Overlay'de de ilerleme göster
                                    if (overlayProgressText != null && !cancellationToken.IsCancellationRequested && !isCancellationRequested)
                                    {
                                        overlayProgressText.Text = $"{currentIndex}/{totalFiles} - {fileName} ({Math.Round(progress * 100, 1)}%)";
                                    }
                                    
                                    // Sadece "Dosya Hash Hesaplama" tab'ı aktifse progress panel'i göster
                                    if (activeTabHeader == "Dosya Hash Hesaplama" && !cancellationToken.IsCancellationRequested && !isCancellationRequested)
                                    {
                                        if (progressPanelFileHash != null)
                                        {
                                            progressPanelFileHash.Visibility = Visibility.Visible;
                                        }
                                        if (txtFooter != null)
                                        {
                                            txtFooter.Visibility = Visibility.Collapsed;
                                        }
                                        if (progressBarFileHash != null && progressBarFileHash.Value < totalProgress)
                                        {
                                            progressBarFileHash.Value = Math.Min(totalProgress, baseProgressPercent + fileProgressRange);
                                        }
                                        if (txtProgressFileHash != null)
                                        {
                                            txtProgressFileHash.Text = $"{currentIndex}/{totalFiles} - {fileName} ({Math.Round(progress * 100, 1)}%)";
                                        }
                                    }
                                }, System.Windows.Threading.DispatcherPriority.Normal);
                            }
                        });
                    }, cancellationToken);
                    
                    // İptal kontrolü - Sonuçları eklemeden önce kontrol et
                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                    {
                        break; // Döngüden çık
                    }
                    
                    // HashInfo null veya iptal edilmiş ise atla
                    if (hashInfo == null || 
                        (hashInfo.MD5Hash == "(İptal edildi)" && hashInfo.SHA1Hash == "(İptal edildi)"))
                    {
                        break; // Döngüden çık
                    }
                    
                    // Sonuçları ekle ve hesaplanan dosyalar listesine ekle
                    if (hashInfo != null && !cancellationToken.IsCancellationRequested && !isCancellationRequested &&
                        hashInfo.MD5Hash != "(İptal edildi)" && hashInfo.SHA1Hash != "(İptal edildi)")
                    {
                        try
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                // İptal kontrolü - UI thread'inde de kontrol et
                                if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                                {
                                    return;
                                }
                                
                                // UI kontrollerine güvenli erişim
                                if (hashResultItems != null && !cancellationToken.IsCancellationRequested && !isCancellationRequested)
                                {
                                    var hashResultItem = new HashResultItem
                                    {
                                        FileName = fileName,
                                        FilePath = filePath,
                                        HashInfo = hashInfo,
                                        HashDate = hashInfo.HashDate
                                    };
                                    hashResultItem.UpdateHashResult(useMD5, useSHA1, pdfShowDateTime);
                                    hashResultItems.Add(hashResultItem);
                                }
                                
                                // MaxWidth'i güncelle - İptal kontrolü ile
                                if (!cancellationToken.IsCancellationRequested && !isCancellationRequested)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        if (!cancellationToken.IsCancellationRequested && !isCancellationRequested)
                                        {
                                            UpdateHashResultItemMaxWidths();
                                        }
                                    }), DispatcherPriority.Loaded);
                                }
                            
                                // Hesaplanan dosyalar listesine ekle
                                if (!cancellationToken.IsCancellationRequested && !isCancellationRequested && calculatedFiles != null)
                                {
                                    string normalizedPath = Path.GetFullPath(filePath.Trim());
                                    calculatedFiles.Add(normalizedPath);
                                }
                            }, System.Windows.Threading.DispatcherPriority.Normal);
                        }
                        catch (OperationCanceledException)
                        {
                            // İptal edildi (OperationCanceledException veya TaskCanceledException)
                            // TaskCanceledException, OperationCanceledException'ın alt sınıfı olduğu için
                            // tek bir catch bloğu yeterli
                            break;
                        }
                    }
                    
                    // İptal kontrolü - Progress güncellemesinden önce kontrol et
                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                    {
                        break; // Döngüden çık
                    }
                    
                    // Son progress güncelle - tamamlandı (yavaş yavaş %100'e çıkar)
                    double finalProgressPercent = (currentIndex / (double)totalFiles) * 100.0;
                    
                    // Progress'i yavaş yavaş %100'e çıkar (animasyon için) - Sadece aktif tab'da göster
                    if (!cancellationToken.IsCancellationRequested && !isCancellationRequested)
                    {
                        double currentValue = await Dispatcher.InvokeAsync(() => 
                        {
                            if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                                return 0.0;
                            return progressBarFileHash != null ? progressBarFileHash.Value : 0.0;
                        });
                        
                        while (currentValue < finalProgressPercent - 0.5 && !cancellationToken.IsCancellationRequested && !isCancellationRequested)
                        {
                            currentValue = Math.Min(finalProgressPercent, currentValue + 2.0);
                            double displayValue = currentValue;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                // İptal kontrolü
                                if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                                {
                                    return;
                                }
                                
                                // Sadece "Dosya Hash Hesaplama" tab'ı aktifse progress panel'i göster
                                if (activeTabHeader == "Dosya Hash Hesaplama")
                                {
                                    if (progressPanelFileHash != null)
                                    {
                                        progressPanelFileHash.Visibility = Visibility.Visible;
                                    }
                                    if (txtFooter != null)
                                    {
                                        txtFooter.Visibility = Visibility.Collapsed;
                                    }
                                    if (progressBarFileHash != null)
                                    {
                                        progressBarFileHash.Value = displayValue;
                                    }
                                }
                            });
                            
                            // İptal kontrolü - Delay'den önce kontrol et
                            if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                            {
                                break;
                            }
                            
                            await Task.Delay(20); // Her 20ms'de bir güncelle
                        }
                    }
                    
                    // İptal kontrolü - Final güncellemeden önce kontrol et
                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                    {
                        break; // Döngüden çık
                    }
                    
                    // Final güncelleme - Sadece aktif tab'da göster
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // İptal kontrolü
                        if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                        {
                            return;
                        }
                        
                        // Sadece "Dosya Hash Hesaplama" tab'ı aktifse progress panel'i göster
                        if (activeTabHeader == "Dosya Hash Hesaplama")
                        {
                            progressPanelFileHash.Visibility = Visibility.Visible;
                            txtFooter.Visibility = Visibility.Collapsed;
                            progressBarFileHash.Value = finalProgressPercent;
                            txtProgressFileHash.Text = $"{currentIndex}/{totalFiles} - {fileName} tamamlandı!";
                        }
                    });
                    
                    // İptal kontrolü - Delay'den önce kontrol et
                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                    {
                        break; // Döngüden çık
                    }
                    
                    // Kısa bir gecikme ekle (UI güncellemesi için)
                    await Task.Delay(100);
                }

                // İptal kontrolü - Son güncellemeden önce kontrol et
                if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                {
                    return; // Metoddan çık
                }

                // Son güncelleme - %100 göster - Sadece aktif tab'da göster
                await Dispatcher.InvokeAsync(() =>
                {
                    // İptal kontrolü
                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                    {
                        return;
                    }
                    // Hesaplama tamamlandıktan sonra dosya listesini temizle
                    // Tüm dosyaları kaldır, sadece 1 boş dosya item'ı bırak
                    var filesToRemove = fileItems.Where(f => !string.IsNullOrEmpty(f.FilePath)).ToList();
                    foreach (var fileToRemove in filesToRemove)
                    {
                        fileItems.Remove(fileToRemove);
                    }
                    
                    // Boş dosya item'larını kontrol et
                    var emptyFileItems = fileItems.Where(f => string.IsNullOrEmpty(f.FilePath)).ToList();
                    if (emptyFileItems.Count == 0)
                    {
                        // Hiç boş item yoksa, 1 tane ekle (X butonu gizli - tek item olduğu için)
                        var newEmptyItem = new FileItem
                        {
                            Label = "Dosya 1:",
                            FilePath = "",
                            CanRemove = false // Tek item olduğu için X butonu gizli
                        };
                        fileItems.Add(newEmptyItem);
                    }
                    else if (emptyFileItems.Count > 1)
                    {
                        // Birden fazla boş item varsa, sadece 1 tane bırak (diğerlerini kaldır)
                        for (int i = 1; i < emptyFileItems.Count; i++)
                        {
                            fileItems.Remove(emptyFileItems[i]);
                        }
                        // Kalan tek item'ın X butonu gizli olmalı
                        if (emptyFileItems.Count > 0)
                        {
                            emptyFileItems[0].CanRemove = false;
                        }
                    }
                    else
                    {
                        // Eğer zaten 1 tane boş item varsa, X butonu gizli olmalı
                        emptyFileItems[0].CanRemove = false;
                    }
                    
                    // Dosya etiketlerini güncelle
                    UpdateFileLabels();
                    
                    // Overlay'de tamamlandı mesajı göster ve butonu göster
                    if (overlayBorder != null)
                    {
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "✅ İşlem Tamamlandı!";
                        }
                        if (overlayProgressText != null)
                        {
                            overlayProgressText.Text = $"Tüm dosyalar başarıyla hesaplandı!\nToplam: {totalFiles} dosya";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Collapsed;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Visible;
                        }
                        // Tamam butonu göründüğünde İptal butonunu gizle
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Collapsed;
                        }
                    }
                    
                    // Sadece "Dosya Hash Hesaplama" tab'ı aktifse progress panel'i göster
                    if (activeTabHeader == "Dosya Hash Hesaplama")
                    {
                        progressPanelFileHash.Visibility = Visibility.Visible;
                        txtFooter.Visibility = Visibility.Collapsed;
                        progressBarFileHash.Value = 100;
                        txtProgressFileHash.Text = $"Tüm dosyalar tamamlandı! ({totalFiles}/{totalFiles})";
                    }
                    
                    // Butonu tekrar enable et - Yeni hesaplama yapılabilir
                    btnCalculateFileHash.IsEnabled = true;
                });
                
                // Overlay'de "Tamam" butonuna basılana kadar bekle - Otomatik kapanma yok
            }
            catch (OperationCanceledException)
            {
                // İptal edildi - OverlayCancelButton_Click zaten UI'ı temizledi, sadece temizlik yap
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        // Overlay'i kapat (OverlayCancelButton_Click zaten kapatmış olabilir ama emin olmak için)
                        try
                        {
                            if (overlayBorder != null)
                            {
                                overlayBorder.Visibility = Visibility.Collapsed;
                            }
                            if (overlayCancelButton != null)
                            {
                                overlayCancelButton.Visibility = Visibility.Collapsed;
                            }
                            if (overlayOkButton != null)
                            {
                                overlayOkButton.Visibility = Visibility.Collapsed;
                            }
                            if (overlayProgressBar != null)
                            {
                                overlayProgressBar.Visibility = Visibility.Collapsed;
                            }
                        }
                        catch
                        {
                            // UI güncellemesi sırasında hata oluştu, sessizce devam et
                        }
                    });
                }
                catch
                {
                    // Dispatcher.Invoke sırasında hata oluştu, sessizce devam et
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // OperationCanceledException dışındaki exception'ları yakala
                Dispatcher.Invoke(() =>
                {
                    progressPanelFileHash.Visibility = Visibility.Collapsed;
                    txtFooter.Visibility = Visibility.Visible;
                    progressBarFileHash.Value = 0;
                    btnCalculateFileHash.IsEnabled = true;
                    // Footer'ı normale döndür
                    footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(232, 232, 232)); // Normal gri
                });
                System.Windows.MessageBox.Show($"Hata: {ex.Message}", "Hata", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // İptal flag'ini sıfırla
                isCancellationRequested = false;
                
                // İptal token'ını temizle
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Dispose();
                    cancellationTokenSource = null;
                }
            }
        }
        
        // Sonuçtan kaldırma - Seçilen sonucu listeden kaldırır
        private void BtnRemoveResult_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Button btn = sender as System.Windows.Controls.Button;
            if (btn != null && btn.Tag is HashResultItem resultItem)
            {
                // Sonuçtan kaldır
                hashResultItems.Remove(resultItem);
                
                // Hesaplanan dosyalar listesinden de kaldır (tekrar hesaplanabilir hale getir)
                if (!string.IsNullOrEmpty(resultItem.FilePath))
                {
                    string normalizedPath = Path.GetFullPath(resultItem.FilePath.Trim());
                    calculatedFiles.Remove(normalizedPath);
                }
            }
        }
        
        // Toplu PDF butonu görünürlüğünü güncelle
        private void UpdateTopluPdfButtonVisibility()
        {
            Dispatcher.Invoke(() =>
            {
                if (btnExportAllToPdf != null && hashResultItems != null)
                {
                    // Birden fazla sonuç varsa butonu göster
                    btnExportAllToPdf.Visibility = hashResultItems.Count > 1 
                        ? Visibility.Visible 
                        : Visibility.Collapsed;
                }
            });
        }
        
        // PDF export overlay göster
        private void ShowPdfExportOverlay(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (overlayBorder != null)
                {
                    overlayBorder.Visibility = Visibility.Visible;
                    if (overlayTitle != null)
                    {
                        overlayTitle.Text = "PDF Oluşturuluyor...";
                    }
                    if (overlayProgressText != null)
                    {
                        overlayProgressText.Text = message;
                    }
                    if (overlayProgressBar != null)
                    {
                        overlayProgressBar.Visibility = Visibility.Visible;
                        overlayProgressBar.IsIndeterminate = true;
                    }
                    if (overlayOkButton != null)
                    {
                        overlayOkButton.Visibility = Visibility.Collapsed;
                    }
                    if (overlayCancelButton != null)
                    {
                        overlayCancelButton.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }
        
        // PDF export overlay gizle
        private void HidePdfExportOverlay(string successMessage = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (overlayBorder != null)
                {
                    if (overlayTitle != null)
                    {
                        overlayTitle.Text = successMessage != null ? "✅ PDF Oluşturuldu!" : "PDF Oluşturuluyor...";
                    }
                    if (overlayProgressText != null)
                    {
                        overlayProgressText.Text = successMessage ?? "PDF oluşturuldu.";
                    }
                    if (overlayProgressBar != null)
                    {
                        overlayProgressBar.Visibility = Visibility.Collapsed;
                    }
                    if (overlayOkButton != null)
                    {
                        overlayOkButton.Visibility = Visibility.Visible;
                    }
                    if (overlayCancelButton != null)
                    {
                        overlayCancelButton.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }
        
        // Toplu PDF çıkarma
        private void BtnExportAllToPdf_Click(object sender, RoutedEventArgs e)
        {
            if (hashResultItems == null || hashResultItems.Count == 0)
            {
                System.Windows.MessageBox.Show("PDF çıkarılacak sonuç bulunamadı.", "Uyarı", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Dosya kaydetme dialog'u
                Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PDF Dosyası (*.pdf)|*.pdf",
                    FileName = $"Toplu_Hash_Sonuclari_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                    DefaultExt = "pdf"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    // Overlay göster
                    ShowPdfExportOverlay("Toplu PDF oluşturuluyor...");
                    
                    // PDF oluşturmayı async yap
                    Task.Run(() =>
                    {
                        try
                        {
                            // Toplu PDF oluştur
                            CreatePdfFromAllHashResults(hashResultItems.ToList(), saveFileDialog.FileName);
                            
                            // Overlay'i güncelle
                            Dispatcher.Invoke(() =>
                            {
                                HidePdfExportOverlay($"{hashResultItems.Count} dosyanın hash sonuçları başarıyla PDF'e kaydedildi!");
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                HidePdfExportOverlay();
                                System.Windows.MessageBox.Show($"Toplu PDF kaydetme hatası: {ex.Message}", "Hata", 
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Toplu PDF kaydetme hatası: {ex.Message}", "Hata", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        // PDF olarak kaydetme
        private void BtnSaveAsPdf_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.Button btn = sender as System.Windows.Controls.Button;
            if (btn != null && btn.Tag is HashResultItem resultItem)
            {
                try
                {
                    // Dosya kaydetme dialog'u
                    Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "PDF Dosyası (*.pdf)|*.pdf",
                        FileName = $"{Path.GetFileNameWithoutExtension(resultItem.FileName)}_hash.pdf",
                        DefaultExt = "pdf"
                    };

                    if (saveFileDialog.ShowDialog() == true)
                    {
                        // Overlay göster
                        ShowPdfExportOverlay("PDF oluşturuluyor...");
                        
                        // PDF oluşturmayı async yap
                        Task.Run(() =>
                        {
                            try
                            {
                                // PDF oluştur
                                CreatePdfFromHashResult(resultItem, saveFileDialog.FileName);
                                
                                // Overlay'i güncelle
                                Dispatcher.Invoke(() =>
                                {
                                    HidePdfExportOverlay("PDF başarıyla oluşturuldu!");
                                });
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    HidePdfExportOverlay();
                                    System.Windows.MessageBox.Show($"PDF kaydetme hatası: {ex.Message}", "Hata", 
                                        MessageBoxButton.OK, MessageBoxImage.Error);
                                });
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"PDF kaydetme hatası: {ex.Message}", "Hata", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        
        // Hash sonucunu PDF olarak oluştur
        private void CreatePdfFromHashResult(HashResultItem resultItem, string filePath)
        {
            try
            {
                // Aktif algoritmaları belirle (başlık ve yön için)
                var activeAlgos = new List<string>();
                if (useMD5) activeAlgos.Add("MD5");
                if (useSHA1) activeAlgos.Add("SHA1");
                string algorithmsHeader = activeAlgos.Count > 0
                    ? "Algoritmalar: " + string.Join(", ", activeAlgos)
                    : "Algoritmalar: (Seçili yok)";

                // PrintDocument kullanarak PDF oluştur
                var printDoc = new PrintDocument();
                
                // PDF yazıcısı bul
                string pdfPrinter = null;
                foreach (string printer in PrinterSettings.InstalledPrinters)
                {
                    if (printer.ToLower().Contains("microsoft print to pdf") || 
                        printer.ToLower().Contains("pdf") ||
                        printer.ToLower().Contains("adobe pdf"))
                    {
                        pdfPrinter = printer;
                        break;
                    }
                }

                if (pdfPrinter == null)
                {
                    // PDF yazıcısı yoksa, basit bir metin tabanlı PDF oluştur
                    CreateSimplePdf(resultItem, filePath);
                    return;
                }

                printDoc.PrinterSettings.PrinterName = pdfPrinter;
                printDoc.PrinterSettings.PrintToFile = true;
                printDoc.PrinterSettings.PrintFileName = filePath;
                // Dosya hash hesaplama için dikey (Portrait)
                printDoc.DefaultPageSettings.Landscape = false;
                
                string content = resultItem.HashResult;
                string fileName = resultItem.FileName;
                
                bool isFirstPage = true;
                int currentLineIndex = 0;
                string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                
                printDoc.PrintPage += (sender, e) =>
                {
                    var fontFamily = new System.Drawing.FontFamily("Arial");
                    var font = new System.Drawing.Font(fontFamily, 11);
                    var titleFont = new System.Drawing.Font(fontFamily, 20, System.Drawing.FontStyle.Bold);
                    var headerFont = new System.Drawing.Font(fontFamily, 11);
                    var fileNameFont = new System.Drawing.Font(fontFamily, 12, System.Drawing.FontStyle.Bold);
                    
                    // Portrait için optimize edilmiş margin'ler (mm cinsinden)
                    // Sol: 25mm, Sağ: 25mm, Üst: 20mm, Alt: 20mm
                    float leftMargin = e.MarginBounds.Left + 20; // 20px ekstra padding
                    float rightMargin = e.MarginBounds.Left + e.MarginBounds.Width - 20; // Sağdan 20px padding
                    float topMargin = e.MarginBounds.Top + 20; // Üstten 20px padding
                    float bottomMargin = e.MarginBounds.Bottom - 20; // Alttan 20px padding (footer için)
                    float yPos = topMargin;
                    
                    // İlk sayfada başlık göster
                    if (isFirstPage)
                    {
                        // Başlık (ayarlardan)
                        e.Graphics.DrawString(pdfTitle, titleFont, 
                            System.Drawing.Brushes.Black, leftMargin, yPos);
                        yPos += 35;
                        
                        // Dosya Numarası (varsa)
                        if (!string.IsNullOrWhiteSpace(pdfFileNumber))
                        {
                            e.Graphics.DrawString(pdfFileNumber, headerFont, 
                                System.Drawing.Brushes.Gray, leftMargin, yPos);
                            yPos += 20;
                        }
                        
                        // Kurum (varsa)
                        if (!string.IsNullOrWhiteSpace(pdfOrganization))
                        {
                            e.Graphics.DrawString(pdfOrganization, headerFont, 
                                System.Drawing.Brushes.Gray, leftMargin, yPos);
                            yPos += 20;
                        }
                        
                        yPos += 10;
                        
                        // Çizgi - Tam genişlikte
                        e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                            leftMargin, yPos, rightMargin, yPos);
                        yPos += 20;
                        
                        // Dosya adı
                        if (yPos + 25 > bottomMargin)
                        {
                            // Dosya adı için yer yoksa yeni sayfaya geç
                            e.HasMorePages = true;
                            isFirstPage = false;
                            return;
                        }
                        
                        e.Graphics.DrawString($"Dosya: {fileName}", fileNameFont, 
                            System.Drawing.Brushes.Black, leftMargin, yPos);
                        yPos += 25;
                        
                        // Dosya adından sonra çizgi - Tam genişlikte
                        e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.LightGray, 1), 
                            leftMargin, yPos, rightMargin, yPos);
                        yPos += 15;
                        
                        isFirstPage = false;
                    }
                    else
                    {
                        // Sonraki sayfalarda sadece dosya adını tekrar göster
                        if (yPos + 25 > bottomMargin)
                        {
                            e.HasMorePages = true;
                            return;
                        }
                        
                        e.Graphics.DrawString($"Dosya: {fileName}", fileNameFont, 
                            System.Drawing.Brushes.Black, leftMargin, yPos);
                        yPos += 25;
                        
                        e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.LightGray, 1), 
                            leftMargin, yPos, rightMargin, yPos);
                        yPos += 15;
                    }
                    
                    // Hash içeriği - Kaldığı yerden devam et
                    while (currentLineIndex < lines.Length)
                    {
                        var line = lines[currentLineIndex];
                        
                        // Sayfa sonu kontrolü - Bir sonraki satır için yer var mı?
                        if (yPos + 18 > bottomMargin)
                        {
                            e.HasMorePages = true;
                            return; // Bu satırı yazmadan yeni sayfaya geç
                        }
                        
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            yPos += 10;
                        }
                        else
                        {
                            // Satır çok uzunsa kelime kelime böl
                            var wrappedLines = WrapText(line, font, rightMargin - leftMargin, e.Graphics);
                            foreach (var wrappedLine in wrappedLines)
                            {
                                if (yPos + 18 > bottomMargin)
                                {
                                    e.HasMorePages = true;
                                    return;
                                }
                                
                                e.Graphics.DrawString(wrappedLine, font, System.Drawing.Brushes.Black, 
                                    leftMargin, yPos);
                                yPos += 18;
                            }
                        }
                        
                        currentLineIndex++;
                    }
                    
                    e.HasMorePages = false;
                };
                
                // Sessiz yazdırma - PrintDialog gösterme
                Task.Run(() =>
                {
                    try
                    {
                        printDoc.Print();
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            HidePdfExportOverlay();
                            System.Windows.MessageBox.Show($"PDF yazdırma hatası: {ex.Message}", "Hata", 
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch
            {
                // PrintDocument başarısız olursa basit PDF oluştur
                CreateSimplePdf(resultItem, filePath);
            }
        }
        
        // Tüm hash sonuçlarını tek bir PDF'e çıkar
        private void CreatePdfFromAllHashResults(List<HashResultItem> resultItems, string filePath)
        {
            try
            {
                // Aktif algoritmaları belirle
                var activeAlgos = new List<string>();
                if (useMD5) activeAlgos.Add("MD5");
                if (useSHA1) activeAlgos.Add("SHA1");
                string algorithmsHeader = activeAlgos.Count > 0
                    ? "Algoritmalar: " + string.Join(", ", activeAlgos)
                    : "Algoritmalar: (Seçili yok)";

                // PrintDocument kullanarak PDF oluştur
                var printDoc = new PrintDocument();
                
                // PDF yazıcısı bul
                string pdfPrinter = null;
                foreach (string printer in PrinterSettings.InstalledPrinters)
                {
                    if (printer.ToLower().Contains("microsoft print to pdf") || 
                        printer.ToLower().Contains("pdf") ||
                        printer.ToLower().Contains("adobe pdf"))
                    {
                        pdfPrinter = printer;
                        break;
                    }
                }

                if (pdfPrinter == null)
                {
                    // PDF yazıcısı yoksa, basit bir metin tabanlı PDF oluştur
                    CreateSimplePdfForAll(resultItems, filePath);
                    return;
                }

                printDoc.PrinterSettings.PrinterName = pdfPrinter;
                printDoc.PrinterSettings.PrintToFile = true;
                printDoc.PrinterSettings.PrintFileName = filePath;
                // Toplu dosya hash hesaplama için dikey (Portrait)
                printDoc.DefaultPageSettings.Landscape = false;
                
                // PrintDialog'u gizle - Sessiz yazdırma
                printDoc.PrinterSettings.PrintToFile = true;
                
                int currentItemIndex = 0;
                bool isFirstPage = true;
                int currentPageNumber = 1;
                const float lineSpacing = 18;
                const float itemSpacing = 15;
                const float footerHeight = 30; // Sayfa numarası için alan
                
                printDoc.PrintPage += (sender, e) =>
                {
                    var fontFamily = new System.Drawing.FontFamily("Arial");
                    var font = new System.Drawing.Font(fontFamily, 10);
                    var titleFont = new System.Drawing.Font(fontFamily, 18, System.Drawing.FontStyle.Bold);
                    var headerFont = new System.Drawing.Font(fontFamily, 11, System.Drawing.FontStyle.Bold);
                    var itemTitleFont = new System.Drawing.Font(fontFamily, 11, System.Drawing.FontStyle.Bold);
                    var footerFont = new System.Drawing.Font(fontFamily, 9);
                    
                    // Portrait için optimize edilmiş margin'ler
                    // Sol: 25mm, Sağ: 25mm, Üst: 20mm, Alt: 20mm
                    float leftMargin = e.MarginBounds.Left + 20; // 20px ekstra padding
                    float rightMargin = e.MarginBounds.Left + e.MarginBounds.Width - 20; // Sağdan 20px padding
                    float topMargin = e.MarginBounds.Top + 20; // Üstten 20px padding
                    float bottomMargin = e.MarginBounds.Bottom - footerHeight; // Footer için alan bırak
                    float yPos = topMargin;
                    
                    // İlk sayfada başlık göster
                    if (isFirstPage)
                    {
                        // Başlık (ayarlardan)
                        e.Graphics.DrawString(pdfTitle, titleFont, 
                            System.Drawing.Brushes.Black, leftMargin, yPos);
                        yPos += 35;
                        
                        // Dosya Numarası (varsa)
                        if (!string.IsNullOrWhiteSpace(pdfFileNumber))
                        {
                            e.Graphics.DrawString(pdfFileNumber, headerFont, 
                                System.Drawing.Brushes.Gray, leftMargin, yPos);
                            yPos += lineSpacing;
                        }
                        
                        // Organizasyon (varsa)
                        if (!string.IsNullOrWhiteSpace(pdfOrganization))
                        {
                            e.Graphics.DrawString(pdfOrganization, headerFont, 
                                System.Drawing.Brushes.Gray, leftMargin, yPos);
                            yPos += lineSpacing;
                        }
                        
                        // Algoritmalar
                        e.Graphics.DrawString(algorithmsHeader, headerFont, 
                            System.Drawing.Brushes.Gray, leftMargin, yPos);
                        yPos += lineSpacing + 10;
                        
                        // Ayırıcı çizgi - Tam genişlikte
                        e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                            leftMargin, yPos, rightMargin, yPos);
                        yPos += 20;
                        
                        isFirstPage = false;
                    }
                    
                    // Her bir hash sonucunu ekle
                    while (currentItemIndex < resultItems.Count)
                    {
                        var item = resultItems[currentItemIndex];
                        
                        // Önce tüm dosya bilgisinin yüksekliğini hesapla
                        string hashText = item.HashResult;
                        string[] hashLines = hashText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                        
                        float itemHeight = lineSpacing + 5; // Dosya adı için
                        foreach (var line in hashLines)
                        {
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                itemHeight += 8;
                                continue;
                            }
                            
                            // Satır çok uzunsa kelime kelime böl ve yüksekliği hesapla
                            var wrappedLines = WrapText(line, font, rightMargin - leftMargin, e.Graphics);
                            itemHeight += wrappedLines.Count * lineSpacing;
                        }
                        
                        // Dosyalar arası ayırıcı için alan
                        if (currentItemIndex < resultItems.Count - 1)
                        {
                            itemHeight += itemSpacing * 2; // Çizgi + boşluk
                        }
                        
                        // Eğer tüm dosya bilgisi sayfa sonuna sığmıyorsa, yeni sayfaya geç
                        if (yPos + itemHeight > bottomMargin)
                        {
                            // Sayfa numarasını göster ve yeni sayfaya geç
                            DrawPageNumber(e, footerFont, currentPageNumber, leftMargin, rightMargin, e.MarginBounds.Bottom - 20);
                            e.HasMorePages = true;
                            currentPageNumber++;
                            return;
                        }
                        
                        // Dosya adı (kalın)
                        e.Graphics.DrawString($"{currentItemIndex + 1}. {item.FileName}", itemTitleFont, 
                            System.Drawing.Brushes.Black, leftMargin, yPos);
                        yPos += lineSpacing + 5;
                        
                        // Hash sonucunu satır satır işle
                        foreach (var line in hashLines)
                        {
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                yPos += 8; // Boş satırlar için daha az boşluk
                                continue;
                            }
                            
                            // Satır çok uzunsa kelime kelime böl
                            var wrappedLines = WrapText(line, font, rightMargin - leftMargin, e.Graphics);
                            foreach (var wrappedLine in wrappedLines)
                            {
                                e.Graphics.DrawString(wrappedLine, font, System.Drawing.Brushes.Black, leftMargin, yPos);
                                yPos += lineSpacing;
                            }
                        }
                        
                        // Dosyalar arası ayırıcı çizgi ve boşluk
                        yPos += itemSpacing;
                        if (currentItemIndex < resultItems.Count - 1) // Son dosya değilse
                        {
                            // Ayırıcı çizgi - Tam genişlikte
                            e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.LightGray, 1), 
                                leftMargin, yPos, rightMargin, yPos);
                            yPos += itemSpacing;
                        }
                        
                        currentItemIndex++;
                    }
                    
                    // Alt bilgi - Sadece son sayfada
                    if (currentItemIndex >= resultItems.Count)
                    {
                        yPos += 15;
                        if (yPos + 30 < bottomMargin)
                        {
                            // Alt çizgi - Tam genişlikte
                            e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                                leftMargin, yPos, rightMargin, yPos);
                            yPos += 15;
                            string footerText = $"Toplam {resultItems.Count} dosya";
                            if (pdfShowDateTime)
                            {
                                footerText += $" - Oluşturulma Tarihi: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                            }
                            e.Graphics.DrawString(footerText, 
                                font, System.Drawing.Brushes.Gray, leftMargin, yPos);
                        }
                    }
                    
                    // Her sayfanın altına sayfa numarası ekle
                    DrawPageNumber(e, footerFont, currentPageNumber, leftMargin, rightMargin, e.MarginBounds.Bottom - 20);
                    
                    e.HasMorePages = currentItemIndex < resultItems.Count;
                    if (e.HasMorePages)
                    {
                        currentPageNumber++;
                    }
                };
                
                // Sessiz yazdırma - PrintDialog gösterme
                Task.Run(() =>
                {
                    try
                    {
                        printDoc.Print();
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            HidePdfExportOverlay();
                            System.Windows.MessageBox.Show($"PDF yazdırma hatası: {ex.Message}", "Hata", 
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                throw new Exception($"PDF oluşturma hatası: {ex.Message}", ex);
            }
        }
        
        // Sayfa numarasını çiz
        private void DrawPageNumber(PrintPageEventArgs e, Font font, int pageNumber, float leftMargin, float rightMargin, float yPos)
        {
            string pageText = $"Sayfa {pageNumber}";
            var textSize = e.Graphics.MeasureString(pageText, font);
            float xPos = (rightMargin + leftMargin) / 2 - textSize.Width / 2; // Ortala
            e.Graphics.DrawString(pageText, font, System.Drawing.Brushes.Gray, xPos, yPos);
        }
        
        // Metni belirli genişliğe göre satırlara böl
        private List<string> WrapText(string text, Font font, float maxWidth, System.Drawing.Graphics graphics)
        {
            var lines = new List<string>();
            
            // Eğer metin zaten maxWidth'den küçükse direkt döndür
            var textSize = graphics.MeasureString(text, font);
            if (textSize.Width <= maxWidth)
            {
                lines.Add(text);
                return lines;
            }
            
            // Hash değerleri gibi boşluksuz uzun metinler için karakter karakter böl
            if (!text.Contains(" ") && text.Length > 50)
            {
                int charsPerLine = (int)(maxWidth / (textSize.Width / text.Length));
                charsPerLine = Math.Max(1, charsPerLine - 2); // Güvenlik marjı
                
                for (int i = 0; i < text.Length; i += charsPerLine)
                {
                    int length = Math.Min(charsPerLine, text.Length - i);
                    lines.Add(text.Substring(i, length));
                }
                return lines;
            }
            
            // Normal metin için kelime kelime böl
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string currentLine = "";
            
            foreach (var word in words)
            {
                string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                var size = graphics.MeasureString(testLine, font);
                
                if (size.Width > maxWidth && !string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(currentLine);
                    currentLine = word;
                }
                else
                {
                    currentLine = testLine;
                }
            }
            
            if (!string.IsNullOrEmpty(currentLine))
            {
                lines.Add(currentLine);
            }
            
            return lines;
        }
        
        // Basit PDF oluştur (PrintDocument kullanılamazsa) - Toplu için
        private void CreateSimplePdfForAll(List<HashResultItem> resultItems, string filePath)
        {
            StringBuilder pdfContent = new StringBuilder();
            pdfContent.AppendLine(pdfTitle);
            pdfContent.AppendLine("=".PadRight(50, '='));
            pdfContent.AppendLine();
            
            if (!string.IsNullOrWhiteSpace(pdfFileNumber))
            {
                pdfContent.AppendLine(pdfFileNumber);
            }
            
            if (!string.IsNullOrWhiteSpace(pdfOrganization))
            {
                pdfContent.AppendLine(pdfOrganization);
            }
            
            var activeAlgos = new List<string>();
            if (useMD5) activeAlgos.Add("MD5");
            if (useSHA1) activeAlgos.Add("SHA1");
            if (activeAlgos.Count > 0)
            {
                pdfContent.AppendLine($"Algoritmalar: {string.Join(", ", activeAlgos)}");
            }
            pdfContent.AppendLine();
            pdfContent.AppendLine("-".PadRight(50, '-'));
            pdfContent.AppendLine();
            
            // Her bir sonucu ekle
            for (int i = 0; i < resultItems.Count; i++)
            {
                var item = resultItems[i];
                pdfContent.AppendLine($"{i + 1}. {item.FileName}");
                pdfContent.AppendLine(item.HashResult);
                pdfContent.AppendLine();
                pdfContent.AppendLine("-".PadRight(50, '-'));
                pdfContent.AppendLine();
            }
            
            pdfContent.AppendLine($"Toplam {resultItems.Count} dosya");
            if (pdfShowDateTime)
            {
                pdfContent.AppendLine($"Oluşturulma Tarihi: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            
            File.WriteAllText(filePath.Replace(".pdf", ".txt"), pdfContent.ToString(), Encoding.UTF8);
            
            System.Windows.MessageBox.Show(
                "PDF yazıcısı bulunamadı. Metin dosyası olarak kaydedildi.\n\n" +
                "PDF oluşturmak için sisteminizde 'Microsoft Print to PDF' yazıcısının yüklü olması gerekir.",
                "Bilgi",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        
        // Basit PDF oluştur (PrintDocument kullanılamazsa)
        private void CreateSimplePdf(HashResultItem resultItem, string filePath)
        {
            // Basit bir metin dosyası oluştur (PDF benzeri)
            StringBuilder pdfContent = new StringBuilder();
            pdfContent.AppendLine(pdfTitle);
            pdfContent.AppendLine("=".PadRight(50, '='));
            pdfContent.AppendLine();
            
            if (!string.IsNullOrWhiteSpace(pdfFileNumber))
            {
                pdfContent.AppendLine(pdfFileNumber);
            }
            
            if (!string.IsNullOrWhiteSpace(pdfOrganization))
            {
                pdfContent.AppendLine(pdfOrganization);
            }
            pdfContent.AppendLine();
            pdfContent.AppendLine("-".PadRight(50, '-'));
            pdfContent.AppendLine();
            pdfContent.AppendLine(resultItem.HashResult);
            pdfContent.AppendLine();
            pdfContent.AppendLine("-".PadRight(50, '-'));
            if (pdfShowDateTime)
            {
                pdfContent.AppendLine($"Oluşturulma Tarihi: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            
            File.WriteAllText(filePath.Replace(".pdf", ".txt"), pdfContent.ToString(), Encoding.UTF8);
            
            System.Windows.MessageBox.Show(
                "PDF yazıcısı bulunamadı. Metin dosyası olarak kaydedildi.\n\n" +
                "PDF oluşturmak için sisteminizde 'Microsoft Print to PDF' yazıcısının yüklü olması gerekir.",
                "Bilgi",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtFolder.Text = dialog.SelectedPath;
                    hashResults.Clear();
                }
            }
        }

        private async void BtnCalculateFolderHash_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtFolder.Text) || !Directory.Exists(txtFolder.Text))
            {
                System.Windows.MessageBox.Show("Lütfen geçerli bir klasör seçin.", "Uyarı", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // İptal flag'ini sıfırla
            isCancellationRequested = false;
            
            // İptal token'ı oluştur
            cancellationTokenSource = new System.Threading.CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            try
            {
                // İşlem başladığını işaretle
                isProcessingFolderHash = true;
                uiUpdateCounter = 0;
                pendingHashResults.Clear(); // Buffer'ı temizle
                allCollectedResults.Clear(); // Tüm sonuçları toplamak için
                lastProgressUpdate = DateTime.MinValue; // Progress throttling reset
                
                // Toplam dosya sayısını hesapla
                totalFiles = 0;
                processedFiles = 0;
                string folderPath = txtFolder.Text;
                baseFolderPath = folderPath; // Base klasör yolunu sakla
                
                await Task.Run(() =>
                {
                    CountFiles(folderPath);
                });
                
                // UI güncellemeleri - Başlangıç durumunu ayarla
                await Dispatcher.InvokeAsync(() =>
                {
                    hashResults.Clear();
                    allHashResults.Clear();
                    
                    // Overlay'i göster
                    if (overlayBorder != null)
                    {
                        overlayBorder.Visibility = Visibility.Visible;
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "İşlem Yapılıyor...";
                        }
                        if (overlayProgressText != null)
                        {
                            overlayProgressText.Text = "Hazırlanıyor...";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Visible;
                            overlayProgressBar.IsIndeterminate = true;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Collapsed;
                        }
                        // İptal butonunu göster
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Visible;
                        }
                    }
                    
                    // Footer'da progress animasyonu göster (yeşil loading) - Sadece aktif tab'da
                    if (activeTabHeader == "Klasör Hash Hesaplama")
                    {
                        progressPanelFileHash.Visibility = Visibility.Visible;
                        txtFooter.Visibility = Visibility.Collapsed;
                    }
                    progressBarFileHash.Maximum = 100;
                    progressBarFileHash.Value = 0;
                    progressBarFileHash.IsIndeterminate = false;
                    txtProgressFileHash.Text = $"Toplam {totalFiles} dosya bulundu. Hash hesaplanıyor...";
                    // txtStatus'u gösterme - sadece txtProgressFileHash göster
                    txtStatus.Visibility = Visibility.Collapsed;
                    // Footer'ı yeşil tonunda renklendir (dosya hesaplamadaki gibi)
                    footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(240, 255, 240)); // Açık yeşil
                });

                await Task.Run(() =>
                {
                    ProcessFolder(folderPath);
                });
                
                // İptal kontrolü - Eğer iptal edildiyse sonuçları ekleme
                if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                {
                    // İptal edildi - Toplanan sonuçları temizle ve DataGrid'e ekleme
                    lock (pendingHashResults)
                    {
                        pendingHashResults.Clear();
                    }
                    allCollectedResults.Clear();
                    return; // İşlemi sonlandır, DataGrid'e ekleme yapma
                }
                
                // Kalan buffer'daki sonuçları topla
                lock (pendingHashResults)
                {
                    if (pendingHashResults.Count > 0)
                    {
                        allCollectedResults.AddRange(pendingHashResults);
                        pendingHashResults.Clear();
                    }
                }
                
                // İptal kontrolü - Tekrar kontrol et (işlem sırasında iptal edilmiş olabilir)
                if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                {
                    // İptal edildi - Toplanan sonuçları temizle ve DataGrid'e ekleme
                    allCollectedResults.Clear();
                    return; // İşlemi sonlandır, DataGrid'e ekleme yapma
                }
                
                // TÜM SONUÇLARI TEK SEFERDE EKLE - UI güncellemesini en sona bırak
                await Dispatcher.InvokeAsync(() =>
                {
                    // İptal kontrolü - UI thread'de tekrar kontrol et
                    if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                    {
                        // İptal edildi - Toplanan sonuçları temizle ve DataGrid'e ekleme
                        allCollectedResults.Clear();
                        return;
                    }
                    
                    // Önce mevcut sonuçları temizle
                    hashResults.Clear();
                    
                    // Tüm toplanan sonuçları ekle (tek seferde - çok daha hızlı)
                    foreach (var item in allCollectedResults)
                    {
                        hashResults.Add(item);
                    }
                    
                    // DataGrid'i güncelle ve otomatik boyutlandır
                    if (dgResults != null)
                    {
                        dgResults.Items.Refresh();
                        dgResults.UpdateLayout();
                        
                        // Layout tamamlandıktan sonra kolonları responsive boyutlandır
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AutoSizeDataGridColumns();
                        }), DispatcherPriority.Loaded);
                    }
                });

                // İptal kontrolü - Eğer iptal edildiyse allHashResults'a ekleme
                if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                {
                    // İptal edildi - allHashResults'ı temizle
                    await Dispatcher.InvokeAsync(() =>
                    {
                        allHashResults.Clear();
                        allCollectedResults.Clear();
                    });
                    return; // İşlemi sonlandır
                }
                
                // UI güncellemeleri - Sonuçları göster (en son yapılıyor)
                await Dispatcher.InvokeAsync(() =>
                {
                    // İptal kontrolü - UI thread'de tekrar kontrol et
                    if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                    {
                        // İptal edildi - allHashResults'ı temizle
                        allHashResults.Clear();
                        allCollectedResults.Clear();
                        return;
                    }
                    
                    // allHashResults'ı temizle ve hashResults'tan kopyala
                    allHashResults.Clear();
                    
                    // Tüm sonuçları allHashResults'a kopyala ve sıra numaralarını set et
                    int rowNumber = 1;
                    foreach (var item in hashResults)
                    {
                        item.RowNumber = rowNumber++; // Sıra numarasını set et (virtualizasyon sorununu önler)
                        allHashResults.Add(item);
                    }
                    int resultCount = hashResults.Count;
                    // txtStatus'u gösterme - sadece txtProgressFileHash göster
                    txtStatus.Visibility = Visibility.Collapsed;
                    
                    // Footer'ı %100'e ayarla
                    progressBarFileHash.Value = 100; // %100 tamamlandı
                    
                    // DataGrid'i tekrar boyutlandır (içerik yüklendikten sonra)
                    if (dgResults != null)
                    {
                        dgResults.UpdateLayout();
                        // Layout tamamlandıktan sonra kolonları boyutlandır
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AutoSizeDataGridColumns();
                        }), DispatcherPriority.Loaded);
                    }
                });
                
                // Kısa bir gecikme (tamamlandı mesajını görmek için)
                await Task.Delay(500);
                
                // Footer'ı normale döndür - İstatistiklerle birlikte
                await Dispatcher.InvokeAsync(() =>
                {
                    // İşlem bittiğini işaretle
                    isProcessingFolderHash = false;
                    
                    // Klasör ve dosya sayılarını hesapla - hashResults kullan (DataGrid'de gösterilen ile aynı)
                    int folderCount = hashResults.Count(r => r.IsFolder);
                    int fileCount = hashResults.Count(r => !r.IsFolder);
                    int totalCount = hashResults.Count;
                    
                    // Overlay'de tamamlandı mesajı göster ve butonu göster
                    if (overlayBorder != null)
                    {
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "✅ İşlem Tamamlandı!";
                        }
                        if (overlayProgressText != null)
                        {
                            overlayProgressText.Text = $"Tüm klasör ve dosyalar başarıyla hesaplandı!\nToplam: {totalCount} kayıt ({folderCount} klasör, {fileCount} dosya)";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Collapsed;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Visible;
                        }
                        // Tamam butonu göründüğünde İptal butonunu gizle
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Collapsed;
                        }
                    }
                    
                    // Sadece aktif tab'da progress panel'i gizle
                    if (activeTabHeader == "Klasör Hash Hesaplama")
                    {
                        progressPanelFileHash.Visibility = Visibility.Collapsed;
                        txtFooter.Visibility = Visibility.Visible;
                    }
                    txtFooter.Text = $"İşlem tamamlandı. Toplam: {totalCount} kayıt ({folderCount} klasör, {fileCount} dosya)";
                    footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(232, 232, 232)); // Normal gri
                    
                    // DataGrid'i tam olarak yenile ve responsive boyutlandır
                    if (dgResults != null)
                    {
                        dgResults.Items.Refresh();
                        dgResults.UpdateLayout();
                        
                        // Layout tamamlandıktan sonra kolonları responsive boyutlandır
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AutoSizeDataGridColumns();
                        }), DispatcherPriority.Loaded);
                    }
                    
                    // Footer'ı güncelle
                    UpdateFolderHashFooter();
                    
                    // Tüm UI'ı zorla yenile
                    this.UpdateLayout();
                    this.InvalidateVisual();
                });
            }
            catch (OperationCanceledException)
            {
                // İptal edildi - OverlayCancelButton_Click zaten UI'ı temizledi, sadece temizlik yap
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isProcessingFolderHash = false;
                        // Overlay'i kapat (OverlayCancelButton_Click zaten kapatmış olabilir ama emin olmak için)
                        try
                        {
                            if (overlayBorder != null)
                            {
                                overlayBorder.Visibility = Visibility.Collapsed;
                            }
                            if (overlayCancelButton != null)
                            {
                                overlayCancelButton.Visibility = Visibility.Collapsed;
                            }
                            if (overlayOkButton != null)
                            {
                                overlayOkButton.Visibility = Visibility.Collapsed;
                            }
                            if (overlayProgressBar != null)
                            {
                                overlayProgressBar.Visibility = Visibility.Collapsed;
                            }
                        }
                        catch
                        {
                            // UI güncellemesi sırasında hata oluştu, sessizce devam et
                        }
                    });
                }
                catch
                {
                    // Dispatcher.InvokeAsync sırasında hata oluştu, sessizce devam et
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // OperationCanceledException dışındaki exception'ları yakala
                await Dispatcher.InvokeAsync(() =>
                {
                    // İşlem bittiğini işaretle
                    isProcessingFolderHash = false;
                    
                    txtStatus.Text = "Hata oluştu.";
                    
                    // Footer'ı normale döndür
                    if (activeTabHeader == "Klasör Hash Hesaplama")
                    {
                        progressPanelFileHash.Visibility = Visibility.Collapsed;
                        txtFooter.Visibility = Visibility.Visible;
                    }
                    txtFooter.Text = "Hata";
                    footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(232, 232, 232));
                });
                System.Windows.MessageBox.Show($"Hata: {ex.Message}", "Hata", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // İptal flag'ini sıfırla
                isCancellationRequested = false;
                
                // İptal token'ını temizle
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Dispose();
                    cancellationTokenSource = null;
                }
            }
        }

        // Dosya ve klasör sayma - Toplam dosya ve klasör sayısını hesaplar (progress bar için)
        private void CountFiles(string folderPath)
        {
            // İptal kontrolü
            if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }
            
            try
            {
                // Bu klasördeki dosyaları say
                string[] files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
                totalFiles += files.Length;

                // Bu klasördeki alt klasörleri say (her klasör bir kayıt olarak ekleniyor)
                string[] subFolders = Directory.GetDirectories(folderPath);
                totalFiles += subFolders.Length;

                // Recursive olarak alt klasörleri de say
                foreach (string subFolder in subFolders)
                {
                    CountFiles(subFolder);
                }
            }
            catch
            {
                // Hata durumunda devam et
            }
        }

        // Klasör işleme - Recursive olarak klasör içindeki tüm dosyaları işler ve hash hesaplar
        private void ProcessFolder(string folderPath)
        {
            // İptal kontrolü
            if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }
            
            try
            {
                // Klasör bilgisini ekle - Klasörün kendisini listelemek için (dosya olmadan)
                string folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName))
                {
                    folderName = folderPath;
                }

                // Relative path hesapla (base klasörden itibaren) - UI thread dışında
                string relativeFolderPath = "";
                if (!string.IsNullOrEmpty(baseFolderPath) && folderPath.StartsWith(baseFolderPath))
                {
                    relativeFolderPath = folderPath.Substring(baseFolderPath.Length).TrimStart('\\');
                    if (string.IsNullOrEmpty(relativeFolderPath))
                    {
                        relativeFolderPath = Path.GetFileName(baseFolderPath);
                    }
                    else
                    {
                        relativeFolderPath = Path.GetFileName(baseFolderPath) + "\\" + relativeFolderPath;
                    }
                }
                else
                {
                    relativeFolderPath = folderName;
                }
                
                // Klasör hash hesaplama - Klasör içindeki dosyaların hash'lerini birleştir
                string folderHashMD5 = CalculateFolderHash(folderPath, useMD5, MD5.Create);
                string folderHashSHA1 = CalculateFolderHash(folderPath, useSHA1, SHA1.Create);
                
                // UI güncellemesi - Büyük veri setleri için throttled (her klasör için değil)
                // Sadece belirli aralıklarla güncelle (UI thread yükünü minimize et)
                bool shouldUpdateOverlayFolder = (processedFiles % UI_UPDATE_BATCH_SIZE == 0);
                if (isProcessingFolderHash && shouldUpdateOverlayFolder)
                {
                    // Yüzde hesapla
                    int percentage = totalFiles > 0 ? (int)((processedFiles * 100.0) / totalFiles) : 0;
                    
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        // Overlay'de klasör yolunu ve yüzdeyi göster
                        if (overlayProgressText != null && !isCancellationRequested)
                        {
                            // Tam yolu göster (relativeFolderPath)
                            string displayPath = relativeFolderPath.Length > 70 ? "..." + relativeFolderPath.Substring(relativeFolderPath.Length - 67) : relativeFolderPath;
                            
                            // Büyük sayılar için formatlama
                            string overlayText = totalFiles > 1000000
                                ? $"{displayPath}\n{processedFiles:N0}/{totalFiles:N0} %{percentage}"
                                : $"{displayPath}\n{processedFiles}/{totalFiles} %{percentage}";
                            
                            overlayProgressText.Text = overlayText;
                        }
                        
                        // UI güncellemesi yapma - Sadece overlay güncelle
                        // Sonuçları topla, en son ekleyeceğiz
                    }));
                }
                
                // Klasör sonucunu topla (UI thread dışında)
                var folderResult = new HashResult
                {
                    FolderPath = relativeFolderPath,
                    FolderName = folderName,
                    FilePath = "",
                    FileName = $"📁 {folderName}", // Klasör adını göster
                    FileExtension = "",
                    MD5Hash = folderHashMD5,
                    SHA1Hash = folderHashSHA1,
                    SHA256Hash = "(Devre dışı)",
                    SHA384Hash = "(Devre dışı)",
                    SHA512Hash = "(Devre dışı)",
                    HashDate = DateTime.Now,
                    ComparisonStatus = "Klasör içeriği hash'i (tüm dosyaların hash'lerinin birleşimi)",
                    IsDifferent = false,
                    IsFolder = true // Klasör işareti
                };
                
                // Thread-safe ekleme
                lock (pendingHashResults)
                {
                    pendingHashResults.Add(folderResult);
                }

                // Dosya hash hesaplama - Klasör içindeki tüm dosyaların hash değerlerini hesaplar
                string[] files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
                foreach (string file in files)
                {
                    try
                    {
                        HashInfo hashInfo = CalculateFileHash(file);
                        string fileName = Path.GetFileName(file);
                        string fileExtension = Path.GetExtension(file);

                        processedFiles++;
                        int percentage = totalFiles > 0 ? (int)((processedFiles * 100.0) / totalFiles) : 0;

                        // Relative path hesapla (base klasörden itibaren) - UI thread dışında
                        // relativeFolderPath zaten üst scope'ta tanımlı, tekrar tanımlamaya gerek yok
                        string relativeFilePath = "";
                        if (!string.IsNullOrEmpty(baseFolderPath) && file.StartsWith(baseFolderPath))
                        {
                            relativeFilePath = file.Substring(baseFolderPath.Length).TrimStart('\\');
                            if (!string.IsNullOrEmpty(relativeFilePath))
                            {
                                relativeFilePath = Path.GetFileName(baseFolderPath) + "\\" + relativeFilePath;
                            }
                        }
                        else
                        {
                            relativeFilePath = file;
                        }
                        
                        // HashResult objesini oluştur
                        var hashResult = new HashResult
                        {
                            FolderPath = relativeFolderPath,
                            FolderName = folderName,
                            FilePath = relativeFilePath,
                            FileName = fileName,
                            FileExtension = string.IsNullOrEmpty(fileExtension) ? "(uzantı yok)" : fileExtension,
                            MD5Hash = hashInfo.MD5Hash,
                            SHA1Hash = hashInfo.SHA1Hash,
                            SHA256Hash = hashInfo.SHA256Hash,
                            SHA384Hash = hashInfo.SHA384Hash,
                            SHA512Hash = hashInfo.SHA512Hash,
                            HashDate = hashInfo.HashDate,
                            ComparisonStatus = "",
                            IsDifferent = false,
                            IsFolder = false // Dosya
                        };
                        
                        // Thread-safe ekleme - UI güncellemesi yapma, sadece topla
                        lock (pendingHashResults)
                        {
                            pendingHashResults.Add(hashResult);
                        }
                        
                        // Overlay güncellemesi - Daha sık güncelle (her 100 dosyada bir veya son dosya)
                        uiUpdateCounter++;
                        // Overlay için daha küçük batch size kullan (daha sık güncelleme)
                        int overlayUpdateBatchSize = Math.Min(100, UI_UPDATE_BATCH_SIZE);
                        bool shouldUpdateOverlay = (uiUpdateCounter % overlayUpdateBatchSize == 0) || (processedFiles == totalFiles);
                        
                        if (shouldUpdateOverlay && isProcessingFolderHash)
                        {
                            // Throttling kontrolü - Saniyede maksimum 2 güncelleme (overlay için daha sık)
                            var now = DateTime.Now;
                            int overlayUpdateInterval = 500; // 500ms = saniyede 2 güncelleme (overlay için)
                            bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= overlayUpdateInterval || processedFiles == totalFiles;
                            
                            if (canUpdate)
                            {
                                lastProgressUpdate = now;
                                
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                {
                                    if (overlayProgressText != null && !isCancellationRequested)
                                    {
                                        // Tam yolu göster (relativeFilePath)
                                        string displayPath = relativeFilePath.Length > 70 ? "..." + relativeFilePath.Substring(relativeFilePath.Length - 67) : relativeFilePath;
                                        
                                        string overlayText = totalFiles > 1000000
                                            ? $"{displayPath}\n{processedFiles:N0}/{totalFiles:N0} %{percentage}"
                                            : $"{displayPath}\n{processedFiles}/{totalFiles} %{percentage}";
                                        overlayProgressText.Text = overlayText;
                                    }
                                    
                                    // Footer'da progress güncelle - Throttled (daha seyrek)
                                    if (activeTabHeader == "Klasör Hash Hesaplama" && isProcessingFolderHash && (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0 || processedFiles == totalFiles))
                                    {
                                        double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                        progressBarFileHash.Value = footerProgress;
                                        
                                        // Büyük sayılar için formatlama
                                        string progressText = totalFiles > 1000000 
                                            ? $"{processedFiles:N0}/{totalFiles:N0} dosya ({Math.Round(footerProgress, 1)}%)"
                                            : $"{processedFiles}/{totalFiles} dosya ({Math.Round(footerProgress, 1)}%)";
                                        txtProgressFileHash.Text = progressText;
                                    }
                                }));
                            }
                        }
                        
                        // Batch toplama - Belirli aralıklarla allCollectedResults'a aktar
                        if (pendingHashResults.Count >= UI_ADD_BATCH_SIZE)
                        {
                            lock (pendingHashResults)
                            {
                                allCollectedResults.AddRange(pendingHashResults);
                                pendingHashResults.Clear();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Dosya okuma hatası işleme - Hata durumunda bile kayıt eklenir
                        processedFiles++;
                        int percentage = totalFiles > 0 ? (int)((processedFiles * 100.0) / totalFiles) : 0;
                        
                        uiUpdateCounter++;
                        bool shouldUpdateUI = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                        
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            // Relative path hesapla (base klasörden itibaren)
                            // relativeFolderPath zaten üst scope'ta tanımlı, tekrar tanımlamaya gerek yok
                            string relativeFilePath = "";
                            if (!string.IsNullOrEmpty(baseFolderPath) && file.StartsWith(baseFolderPath))
                            {
                                relativeFilePath = file.Substring(baseFolderPath.Length).TrimStart('\\');
                                if (!string.IsNullOrEmpty(relativeFilePath))
                                {
                                    relativeFilePath = Path.GetFileName(baseFolderPath) + "\\" + relativeFilePath;
                                }
                            }
                            else
                            {
                                relativeFilePath = file;
                            }
                            
                            hashResults.Add(new HashResult
                            {
                                FolderPath = relativeFolderPath,
                                FolderName = folderName,
                                FilePath = relativeFilePath,
                                FileName = Path.GetFileName(file),
                                FileExtension = Path.GetExtension(file),
                                MD5Hash = $"HATA: {ex.Message}",
                                SHA1Hash = $"HATA: {ex.Message}",
                                SHA256Hash = $"HATA: {ex.Message}",
                                SHA384Hash = $"HATA: {ex.Message}",
                                SHA512Hash = $"HATA: {ex.Message}",
                                HashDate = DateTime.Now,
                                ComparisonStatus = "",
                                IsDifferent = false,
                                IsFolder = false // Dosya
                            });
                            
                            // Footer'da progress güncelle - Batch olarak (performans için)
                            if (shouldUpdateUI && activeTabHeader == "Klasör Hash Hesaplama" && isProcessingFolderHash)
                            {
                                double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                progressBarFileHash.Value = footerProgress;
                                txtProgressFileHash.Text = $"{processedFiles}/{totalFiles} dosya işlendi ({Math.Round(footerProgress, 1)}%)";
                                // txtStatus'u gösterme - sadece txtProgressFileHash göster
                                txtStatus.Visibility = Visibility.Collapsed;
                            }
                        }));
                    }
                }

                // Recursive klasör işleme - Alt klasörleri de işler
                string[] subFolders = Directory.GetDirectories(folderPath);
                foreach (string subFolder in subFolders)
                {
                    // İptal kontrolü
                    if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }
                    
                    ProcessFolder(subFolder);
                }
            }
            catch (Exception ex)
            {
                // Klasör erişim hatası işleme - Erişim hatası durumunda kayıt eklenir
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    hashResults.Add(new HashResult
                    {
                        FolderPath = folderPath,
                        FolderName = Path.GetFileName(folderPath),
                        FilePath = "",
                        FileName = "",
                        MD5Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                        SHA1Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                        SHA256Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                        SHA384Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                        SHA512Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                        HashDate = DateTime.Now,
                        ComparisonStatus = "",
                        IsDifferent = false,
                        IsFolder = true // Klasör işareti
                    });
                }));
            }
        }

        // Klasör hash hesaplama - Adli bilişim standartlarına uygun: Dosya adı + hash birleşimi
        // OPTİMİZE EDİLDİ: Sadece gerekli hash algoritmasını hesapla, büyük buffer, maksimum paralel işlem
        private string CalculateFolderHash(string folderPath, bool isEnabled, Func<HashAlgorithm> hashAlgorithmFactory)
        {
            if (!isEnabled)
            {
                return "(Devre dışı)";
            }
            
            try
            {
                string[] folderFiles = null;
                try
                {
                    folderFiles = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
                }
                catch (UnauthorizedAccessException)
                {
                    return "ERİŞİM HATASI: Klasöre erişilemedi";
                }
                catch
                {
                    return "HATA: Klasör okunamadı";
                }
                
                Array.Sort(folderFiles); // Dosyaları alfabetik sırala (tutarlılık için)
                
                if (folderFiles.Length == 0)
                {
                    return "Klasör - İçerik yok (boş klasör)";
                }
                
                // PARALEL İŞLEME: FTK performansı için dosyaları paralel olarak işle
                // Thread-safe collection kullan (dosya sırası önemli olduğu için Dictionary kullanıyoruz)
                var fileHashResults = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
                
                // ULTRA AGRESIF PARALELLEŞTİRME - 4-5TB veri setleri için FTK/X-Ways seviyesi performans
                // CPU core sayısı * 8 (büyük veri setleri için maksimum paralelleştirme)
                // Minimum 32 thread, maksimum 128 thread (çok büyük sistemler için)
                int maxThreads = Math.Min(Math.Max(Environment.ProcessorCount * 8, 32), 128);
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxThreads
                };
                
                // Dosyaları paralel olarak işle - Sadece gerekli hash algoritmasını hesapla
                Parallel.For(0, folderFiles.Length, parallelOptions, i =>
                {
                    // İptal kontrolü
                    if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                    {
                        return; // İptal edildi, çık
                    }
                    
                    try
                    {
                        string file = folderFiles[i];
                        string fileName = Path.GetFileName(file);
                        
                        // Sadece gerekli hash algoritmasını hesapla (tüm hash'leri değil!)
                        string hashValue = CalculateSingleFileHash(file, hashAlgorithmFactory);
                        
                        // Adli bilişim: Dosya adı + hash birleşimi (dosya adı da hash'e dahil)
                        // String interpolation yerine StringBuilder kullan (performans için)
                        if (!string.IsNullOrEmpty(hashValue) && hashValue != "(Devre dışı)")
                        {
                            // Pre-allocated string builder ile daha hızlı
                            var sb = new StringBuilder(fileName.Length + hashValue.Length + 2);
                            sb.Append(fileName);
                            sb.Append(':');
                            sb.Append(hashValue);
                            sb.Append('|');
                            fileHashResults[i] = sb.ToString();
                        }
                    }
                    catch { }
                });
                
                // Sonuçları sıralı olarak birleştir (alfabetik sıra korunmalı)
                // Kapasite önceden belirlenmiş StringBuilder - Memory allocation optimizasyonu
                int estimatedCapacity = folderFiles.Length * 100; // Her dosya için ~100 karakter tahmin
                StringBuilder allHashes = new StringBuilder(estimatedCapacity);
                for (int i = 0; i < folderFiles.Length; i++)
                {
                    if (fileHashResults.TryGetValue(i, out string hashResult))
                    {
                        allHashes.Append(hashResult);
                    }
                }
                
                if (allHashes.Length > 0)
                {
                    using (var hashAlg = hashAlgorithmFactory())
                    {
                        // String'i byte array'e çevir ve hash'le (dosya adları + hash'ler)
                        string hashString = allHashes.ToString();
                        byte[] hashBytes = hashAlg.ComputeHash(Encoding.UTF8.GetBytes(hashString));
                        
                        // String işlemini optimize et - Replace yerine daha hızlı yöntem
                        var sb = new StringBuilder(hashBytes.Length * 2);
                        foreach (byte b in hashBytes)
                        {
                            sb.Append(b.ToString("x2"));
                        }
                        return sb.ToString();
                    }
                }
                else
                {
                    return "Klasör - Hash hesaplanamadı";
                }
            }
            catch
            {
                return "Klasör - Hash hesaplama hatası";
            }
        }
        
        // Tek bir hash algoritması için ULTRA OPTİMİZE edilmiş dosya hash hesaplama
        // Klasör hash hesaplaması için kullanılır - Maksimum performans için optimize edildi
        private string CalculateSingleFileHash(string filePath, Func<HashAlgorithm> hashAlgorithmFactory)
        {
            try
            {
                // 32MB buffer - 4-5TB veri setleri için ultra performans (FTK/X-Ways seviyesi)
                const int bufferSize = 32 * 1024 * 1024;
                byte[] buffer = new byte[bufferSize];
                int bytesRead;

                // ULTRA OPTİMİZE FileOptions - Büyük veri setleri için:
                // SequentialScan: Sıralı okuma için optimize
                // Asynchronous: Asenkron I/O (maksimum hız)
                // RandomAccess: Büyük dosyalar için optimize (FTK/X-Ways benzeri)
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, 
                    FileOptions.SequentialScan | FileOptions.Asynchronous | FileOptions.RandomAccess))
                using (var hashAlg = hashAlgorithmFactory())
                {
                    // Dosyayı büyük chunk'lar halinde oku ve hash hesapla
                    while ((bytesRead = stream.Read(buffer, 0, bufferSize)) > 0)
                    {
                        hashAlg.TransformBlock(buffer, 0, bytesRead, null, 0);
                    }

                    // Final hash hesaplama
                    hashAlg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    
                    // String işlemini optimize et - Replace yerine daha hızlı yöntem
                    var hashBytes = hashAlg.Hash;
                    var sb = new StringBuilder(hashBytes.Length * 2);
                    foreach (byte b in hashBytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }
                    return sb.ToString();
                }
            }
            catch
            {
                return "(Devre dışı)";
            }
        }
        
        // Hash hesaplama - Seçilen hash algoritmalarına göre dosyanın hash değerlerini hesaplar
        // OPTİMİZE EDİLDİ: Büyük dosyalar için streaming okuma - Tüm hash'ler tek okumada hesaplanır
        private HashInfo CalculateFileHash(string filePath)
        {
            HashInfo hashInfo = new HashInfo();
            hashInfo.HashDate = DateTime.Now;

            // 16MB buffer - Büyük veri setleri için ultra performans
            const int bufferSize = 16 * 1024 * 1024;
            byte[] buffer = new byte[bufferSize];
            int bytesRead;

            // ULTRA OPTİMİZE FileStream - Büyük veri setleri için
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                // Tüm hash algoritmalarını başlat
                HashAlgorithm md5 = null;
                HashAlgorithm sha1 = null;

                try
                {
                    if (useMD5) md5 = MD5.Create();
                    if (useSHA1) sha1 = SHA1.Create();

                    // Dosyayı chunk'lar halinde oku ve tüm hash'leri aynı anda hesapla (TEK OKUMA!)
                    // Bu büyük dosyalar için çok daha hızlı
                    while ((bytesRead = stream.Read(buffer, 0, bufferSize)) > 0)
                    {
                        // Her hash algoritmasına buffer'ı ekle (streaming)
                        if (md5 != null) md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                        if (sha1 != null) sha1.TransformBlock(buffer, 0, bytesRead, null, 0);
                    }

                    // Final hash hesaplama - Optimize edilmiş string dönüşümü
                    if (md5 != null)
                    {
                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var md5Bytes = md5.Hash;
                        var md5Sb = new StringBuilder(md5Bytes.Length * 2);
                        foreach (byte b in md5Bytes)
                        {
                            md5Sb.Append(b.ToString("x2"));
                        }
                        hashInfo.MD5Hash = md5Sb.ToString();
                    }
                    else
                    {
                        hashInfo.MD5Hash = "(Devre dışı)";
                    }

                    if (sha1 != null)
                    {
                        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var sha1Bytes = sha1.Hash;
                        var sha1Sb = new StringBuilder(sha1Bytes.Length * 2);
                        foreach (byte b in sha1Bytes)
                        {
                            sha1Sb.Append(b.ToString("x2"));
                        }
                        hashInfo.SHA1Hash = sha1Sb.ToString();
                    }
                    else
                    {
                        hashInfo.SHA1Hash = "(Devre dışı)";
                    }

                    // SHA256, SHA384, SHA512 devre dışı
                    hashInfo.SHA256Hash = "(Devre dışı)";
                    hashInfo.SHA384Hash = "(Devre dışı)";
                    hashInfo.SHA512Hash = "(Devre dışı)";
                }
                finally
                {
                    // Tüm hash algoritmalarını temizle
                    md5?.Dispose();
                    sha1?.Dispose();
                }
            }

            return hashInfo;
        }

        // Hash hesaplama - Progress callback ile gerçek ilerleme gösterir
        // ULTRA OPTİMİZE: Büyük buffer ve asenkron I/O
        private HashInfo CalculateFileHashWithProgress(string filePath, long fileSize, System.Threading.CancellationToken cancellationToken, Action<double> progressCallback)
        {
            HashInfo hashInfo = new HashInfo();
            hashInfo.HashDate = DateTime.Now;

            // 16MB buffer - Progress gösterimi için ultra optimize
            const int bufferSize = 16 * 1024 * 1024;
            byte[] buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            int bytesRead;

            // ULTRA OPTİMİZE FileStream - Büyük veri setleri için
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                // MD5 Hash hesaplama
                HashAlgorithm md5 = null;
                HashAlgorithm sha1 = null;

                try
                {
                    if (useMD5) md5 = MD5.Create();
                    if (useSHA1) sha1 = SHA1.Create();

                    // Dosyayı chunk'lar halinde oku ve hash hesapla
                    while ((bytesRead = stream.Read(buffer, 0, bufferSize)) > 0)
                    {
                        // İptal kontrolü - Exception throw etme, sadece return yap
                        if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                        {
                            // İptal edildi, hash hesaplamayı durdur
                            // Exception throw etmek yerine boş HashInfo döndür
                            // Bu debugger'ın "unsafe abort" hatası vermesini engeller
                            hashInfo.MD5Hash = "(İptal edildi)";
                            hashInfo.SHA1Hash = "(İptal edildi)";
                            hashInfo.SHA256Hash = "(İptal edildi)";
                            hashInfo.SHA384Hash = "(İptal edildi)";
                            hashInfo.SHA512Hash = "(İptal edildi)";
                            return hashInfo;
                        }
                        
                        totalBytesRead += bytesRead;

                        // Her hash algoritmasına buffer'ı ekle
                        if (md5 != null) md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                        if (sha1 != null) sha1.TransformBlock(buffer, 0, bytesRead, null, 0);

                        // Progress güncelle (her 1MB'da bir veya son chunk'ta)
                        // İptal kontrolü - Progress callback'inden önce kontrol et
                        if (!cancellationToken.IsCancellationRequested && !isCancellationRequested)
                        {
                            if (fileSize > 0 && (totalBytesRead % (1024 * 1024) < bufferSize || totalBytesRead >= fileSize))
                            {
                                double progress = (double)totalBytesRead / fileSize;
                                progressCallback?.Invoke(progress);
                            }
                        }
                    }

                    // İptal kontrolü - Final hash hesaplamadan önce kontrol et
                    if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                    {
                        hashInfo.MD5Hash = "(İptal edildi)";
                        hashInfo.SHA1Hash = "(İptal edildi)";
                        hashInfo.SHA256Hash = "(İptal edildi)";
                        hashInfo.SHA384Hash = "(İptal edildi)";
                        hashInfo.SHA512Hash = "(İptal edildi)";
                        return hashInfo;
                    }

                    // Final hash hesaplama - Optimize edilmiş string dönüşümü
                    if (md5 != null)
                    {
                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var md5Bytes = md5.Hash;
                        var md5Sb = new StringBuilder(md5Bytes.Length * 2);
                        foreach (byte b in md5Bytes)
                        {
                            md5Sb.Append(b.ToString("x2"));
                        }
                        hashInfo.MD5Hash = md5Sb.ToString();
                    }
                    else
                    {
                        hashInfo.MD5Hash = "(Devre dışı)";
                    }

                    if (sha1 != null)
                    {
                        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var sha1Bytes = sha1.Hash;
                        var sha1Sb = new StringBuilder(sha1Bytes.Length * 2);
                        foreach (byte b in sha1Bytes)
                        {
                            sha1Sb.Append(b.ToString("x2"));
                        }
                        hashInfo.SHA1Hash = sha1Sb.ToString();
                    }
                    else
                    {
                        hashInfo.SHA1Hash = "(Devre dışı)";
                    }

                    // SHA256, SHA384, SHA512 devre dışı
                    hashInfo.SHA256Hash = "(Devre dışı)";
                    hashInfo.SHA384Hash = "(Devre dışı)";
                    hashInfo.SHA512Hash = "(Devre dışı)";

                    // %100 progress
                    progressCallback?.Invoke(1.0);
                }
                finally
                {
                    md5?.Dispose();
                    sha1?.Dispose();
                }
            }

            return hashInfo;
        }

        // Hash sonuçlarını formatlama - Sadece seçilen hash'leri kullanıcı dostu formatta gösterir
        private string FormatHashResult(HashInfo hashInfo, string fileName = "")
        {
            StringBuilder sb = new StringBuilder();
            
            // Dosya adını ekle
            if (!string.IsNullOrEmpty(fileName))
            {
                sb.AppendLine($"Dosya: {fileName}");
                sb.AppendLine();
            }
            
            // Sadece seçili ve hesaplanmış hash'leri göster (devre dışı olanları gösterme)
            if (useMD5 && hashInfo.MD5Hash != "(Devre dışı)")
                sb.AppendLine($"MD5: {hashInfo.MD5Hash}");
            if (useSHA1 && hashInfo.SHA1Hash != "(Devre dışı)")
                sb.AppendLine($"SHA1: {hashInfo.SHA1Hash}");
            
            if (pdfShowDateTime)
            {
                sb.AppendLine($"Tarih: {hashInfo.HashDate:yyyy-MM-dd HH:mm:ss}");
            }
            return sb.ToString();
        }
        
        // Mevcut hash sonuçlarını ayarlara göre güncelle
        private void UpdateAllHashResults()
        {
            if (hashResultItems == null) return;
            
            foreach (var item in hashResultItems)
            {
                if (item.HashInfo != null)
                {
                    item.UpdateHashResult(useMD5, useSHA1, pdfShowDateTime);
                }
            }
        }



        // Karşılaştırma Sekmesi Fonksiyonları
        // Klasör 1 seçme - Karşılaştırma için ilk klasörü seçer
        private void BtnSelectFolder1_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtFolder1.Text = dialog.SelectedPath;
                }
            }
        }

        // Klasör 2 seçme - Karşılaştırma için ikinci klasörü seçer
        private void BtnSelectFolder2_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtFolder2.Text = dialog.SelectedPath;
                }
            }
        }

        // İki klasör karşılaştırma - İki klasördeki dosyaları karşılaştırır ve farklılıkları bulur
        private async void BtnCompareFolders_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtFolder1.Text) || !Directory.Exists(txtFolder1.Text) ||
                string.IsNullOrEmpty(txtFolder2.Text) || !Directory.Exists(txtFolder2.Text))
            {
                System.Windows.MessageBox.Show("Lütfen geçerli iki klasör seçin.", "Uyarı", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // İptal flag'ini sıfırla
            isCancellationRequested = false;
            
            // İptal token'ı oluştur
            cancellationTokenSource = new System.Threading.CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            try
            {
                // İşlem başladığını işaretle
                isProcessingCompare = true;
                uiUpdateCounter = 0;
                pendingHashResults.Clear(); // Buffer'ı temizle
                pendingCompareResults.Clear(); // Karşılaştırma buffer'ı temizle
                allCollectedCompareResults.Clear(); // Tüm karşılaştırma sonuçlarını toplamak için
                lastProgressUpdate = DateTime.MinValue; // Progress throttling reset
                
                comparisonDict.Clear();
                folderHashCache.Clear(); // Hash cache'i temizle
                string folder1Path = txtFolder1.Text;
                string folder2Path = txtFolder2.Text;

                // Toplam dosya ve klasör sayısını hesapla - Progress bar için
                totalFiles = 0;
                processedFiles = 0;
                
                await Task.Run(() =>
                {
                    CountFilesAndFolders(folder1Path);
                    CountFilesAndFolders(folder2Path);
                });

                // UI güncellemeleri - Başlangıç durumunu ayarla
                Dispatcher.Invoke(() =>
                {
                    // Önce temizle - Yeni karşılaştırma başlıyor
                    compareResults.Clear();
                    allCompareResults.Clear();
                    comparisonDict.Clear();
                    
                    // Overlay'i göster
                    if (overlayBorder != null)
                    {
                        overlayBorder.Visibility = Visibility.Visible;
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "İşlem Yapılıyor...";
                        }
                        if (overlayProgressText != null)
                        {
                            overlayProgressText.Text = "Hazırlanıyor...";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Visible;
                            overlayProgressBar.IsIndeterminate = true;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Collapsed;
                        }
                        // İptal butonunu göster
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Visible;
                        }
                    }
                    
                    progressPanelCompare.Visibility = Visibility.Visible;
                    progressBarCompare.IsIndeterminate = false;
                    progressBarCompare.Maximum = totalFiles;
                    progressBarCompare.Value = 0;
                    txtProgressCompare.Text = "0% - Hazırlanıyor...";
                    txtStatusCompare.Text = "";
                    
                    // Footer'da ilerleme göster - Sadece aktif tab'da
                    if (activeTabHeader == "İki Klasör Karşılaştırma")
                    {
                        progressPanelFileHash.Visibility = Visibility.Visible;
                        txtFooter.Visibility = Visibility.Collapsed;
                    }
                    progressBarFileHash.IsIndeterminate = false;
                    progressBarFileHash.Maximum = totalFiles;
                    progressBarFileHash.Value = 0;
                    txtProgressFileHash.Text = "0% - Hazırlanıyor...";
                    txtStatus.Visibility = Visibility.Collapsed;
                });

                // İlk klasörü işle - Base path'i parametre olarak geç
                await Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    ProcessFolderForComparison(folder1Path, 1, folder1Path, cancellationToken);
                });

                // İptal kontrolü - Exception throw etme, sadece return yap
                if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                {
                    // İptal edildi, işlemi durdur
                    // Exception throw etmek yerine sadece return yap
                    // Bu debugger'ın "unsafe abort" hatası vermesini engeller
                    return;
                }

                // İkinci klasörü işle ve karşılaştır - Base path'i parametre olarak geç
                await Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    ProcessFolderForComparison(folder2Path, 2, folder2Path, cancellationToken);
                });
                
                // İptal kontrolü - Eğer iptal edildiyse sonuçları ekleme
                if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                {
                    // İptal edildi - Sonuçları temizle
                    await Dispatcher.InvokeAsync(() =>
                    {
                        allCollectedCompareResults.Clear();
                        pendingCompareResults.Clear();
                    });
                    return; // İşlemi sonlandır
                }
                
                // Kalan buffer'daki sonuçları topla
                lock (pendingCompareResults)
                {
                    if (pendingCompareResults.Count > 0)
                    {
                        allCollectedCompareResults.AddRange(pendingCompareResults);
                        pendingCompareResults.Clear();
                    }
                }
                
                // TÜM SONUÇLARI TEK SEFERDE EKLE - UI güncellemesini en sona bırak
                await Dispatcher.InvokeAsync(() =>
                {
                    // İptal kontrolü - UI thread'de tekrar kontrol et
                    if (isCancellationRequested || (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested))
                    {
                        // İptal edildi - Sonuçları temizle
                        allCollectedCompareResults.Clear();
                        pendingCompareResults.Clear();
                        return;
                    }
                    
                    // Önce mevcut sonuçları temizle
                    compareResults.Clear();
                    
                    // Tüm toplanan sonuçları ekle (tek seferde - çok daha hızlı)
                    foreach (var item in allCollectedCompareResults)
                    {
                        compareResults.Add(item);
                    }
                    
                    // DataGrid'i güncelle ve otomatik boyutlandır
                    if (dgCompareResults != null)
                    {
                        dgCompareResults.Items.Refresh();
                        dgCompareResults.UpdateLayout();
                        
                        // Layout tamamlandıktan sonra kolonları responsive boyutlandır
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AutoSizeCompareDataGridColumns();
                        }), DispatcherPriority.Loaded);
                    }
                });

                // Karşılaştırmayı yap ve UI güncelle
                await Dispatcher.InvokeAsync(() =>
                {
                    // allCompareResults'ı temizle ve compareResults'tan kopyala
                    allCompareResults.Clear();
                    foreach (var item in compareResults)
                    {
                        allCompareResults.Add(item);
                    }
                    
                    CompareResults();
                    
                    // compareResults kullan (DataGrid'de gösterilen ile aynı)
                    // allCompareResults sadece filtreleme için kullanılıyor
                    var sourceForCount = compareResults;
                    
                    // Sayıları doğru hesapla
                    int folderCount = sourceForCount.Count(r => r.IsFolder);
                    int fileCount = sourceForCount.Count(r => !r.IsFolder && !string.IsNullOrEmpty(r.FilePath));
                    int totalCount = sourceForCount.Count;
                    
                    // Filtreleme varsayılan olarak "All" olsun ve uygula
                    currentFilterCompare = "All";
                    ApplyFiltersCompare();
                    
                    // DataGrid'i tam olarak yenile - Tüm renkler ve durumlar görünsün
                    RefreshCompareResultsGrid();
                    
                    progressPanelCompare.Visibility = Visibility.Collapsed;
                    progressBarCompare.Value = 0;
                    
                    // Footer'ı güncelle - Sadece aktif tab'da
                    if (activeTabHeader == "İki Klasör Karşılaştırma")
                    {
                        progressPanelFileHash.Visibility = Visibility.Collapsed;
                        txtFooter.Visibility = Visibility.Visible;
                    }
                    progressBarFileHash.Value = 0;
                    
                    txtStatusCompare.Text = $"✅ İşlem tamamlandı! {folderCount} klasör, {fileCount} dosya";
                    
                    // Footer'ı güncelle - allCompareResults kullan
                    UpdateCompareHashFooter();
                });
                
                // Kısa bir gecikme (tamamlandı mesajını görmek için) - klasör hash hesaplamadaki gibi
                await Task.Delay(500);
                
                // Footer'ı normale döndür - İstatistiklerle birlikte (klasör hash hesaplamadaki gibi)
                await Dispatcher.InvokeAsync(() =>
                {
                    // İşlem bittiğini işaretle
                    isProcessingCompare = false;
                    
                    // compareResults kullan (DataGrid'de gösterilen ile aynı)
                    var sourceForCount = compareResults;
                    
                    // Sayıları doğru hesapla
                    int folderCount = sourceForCount.Count(r => r.IsFolder);
                    int fileCount = sourceForCount.Count(r => !r.IsFolder && !string.IsNullOrEmpty(r.FilePath));
                    int totalCount = sourceForCount.Count;
                    
                    // Overlay'de tamamlandı mesajı göster ve butonu göster (klasör hash hesaplamadaki gibi)
                    if (overlayBorder != null)
                    {
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "✅ İşlem Tamamlandı!";
                        }
                        if (overlayProgressText != null)
                        {
                            overlayProgressText.Text = $"Tüm klasör ve dosyalar başarıyla karşılaştırıldı!\nToplam: {totalCount} kayıt ({folderCount} klasör, {fileCount} dosya)";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Collapsed;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Visible;
                        }
                        // Tamam butonu göründüğünde İptal butonunu gizle
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // İptal edildi - OverlayCancelButton_Click zaten UI'ı temizledi, sadece temizlik yap
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        isProcessingCompare = false;
                        // Overlay'i kapat (OverlayCancelButton_Click zaten kapatmış olabilir ama emin olmak için)
                        try
                        {
                            if (overlayBorder != null)
                            {
                                overlayBorder.Visibility = Visibility.Collapsed;
                            }
                            if (overlayCancelButton != null)
                            {
                                overlayCancelButton.Visibility = Visibility.Collapsed;
                            }
                            if (overlayOkButton != null)
                            {
                                overlayOkButton.Visibility = Visibility.Collapsed;
                            }
                            if (overlayProgressBar != null)
                            {
                                overlayProgressBar.Visibility = Visibility.Collapsed;
                            }
                        }
                        catch
                        {
                            // UI güncellemesi sırasında hata oluştu, sessizce devam et
                        }
                    });
                }
                catch
                {
                    // Dispatcher.Invoke sırasında hata oluştu, sessizce devam et
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // OperationCanceledException dışındaki exception'ları yakala
                Dispatcher.Invoke(() =>
                {
                    // İşlem bittiğini işaretle
                    isProcessingCompare = false;
                    
                    progressPanelCompare.Visibility = Visibility.Collapsed;
                    progressBarCompare.Value = 0;
                    
                    // Footer'ı güncelle
                    if (activeTabHeader == "İki Klasör Karşılaştırma")
                    {
                        progressPanelFileHash.Visibility = Visibility.Collapsed;
                        txtFooter.Visibility = Visibility.Visible;
                    }
                    progressBarFileHash.Value = 0;
                    
                    txtStatusCompare.Text = "Hata oluştu.";
                    txtFooter.Text = "Hata";
                });
                System.Windows.MessageBox.Show($"Hata: {ex.Message}", "Hata", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // İptal flag'ini sıfırla
                isCancellationRequested = false;
                
                // İptal token'ını temizle
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Dispose();
                    cancellationTokenSource = null;
                }
            }
        }

        // Overlay "İptal" butonu tıklandığında - İşlemi durdur
        private void OverlayCancelButton_Click(object sender, RoutedEventArgs e)
        {
            // İptal flag'ini set et - UI güncellemelerini engelle
            isCancellationRequested = true;
            
            // İptal token'ını iptal et
            if (cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested)
            {
                cancellationTokenSource.Cancel();
            }
            
            // İşlem durumlarını sıfırla
            isProcessingFolderHash = false;
            isProcessingCompare = false;
            
            // Overlay'i hemen gizle - Kullanıcı iptal etti, overlay'i kapat
            if (overlayBorder != null)
            {
                overlayBorder.Visibility = Visibility.Collapsed;
            }
            
            // Butonları sıfırla
            if (overlayCancelButton != null)
            {
                overlayCancelButton.Visibility = Visibility.Collapsed;
            }
            if (overlayOkButton != null)
            {
                overlayOkButton.Visibility = Visibility.Collapsed;
            }
            
            // Progress bar'ı sıfırla
            if (overlayProgressBar != null)
            {
                overlayProgressBar.Visibility = Visibility.Collapsed;
                overlayProgressBar.IsIndeterminate = false;
                overlayProgressBar.Value = 0;
            }
            
            // Başlık ve mesajı sıfırla
            if (overlayTitle != null)
            {
                overlayTitle.Text = "İşlem Yapılıyor...";
            }
            if (overlayProgressText != null)
            {
                overlayProgressText.Text = "Hesaplanıyor...";
            }
            
            // Aktif tab'a göre butonları enable et ve UI'ı temizle
            if (activeTabHeader == "Dosya Hash Hesaplama")
            {
                if (btnCalculateFileHash != null)
                {
                    btnCalculateFileHash.IsEnabled = true;
                }
                progressPanelFileHash.Visibility = Visibility.Collapsed;
                txtFooter.Visibility = Visibility.Visible;
                progressBarFileHash.Value = 0;
            }
            else if (activeTabHeader == "Klasör Hash Hesaplama")
            {
                if (btnCalculateFolderHash != null)
                {
                    btnCalculateFolderHash.IsEnabled = true;
                }
                progressPanelFileHash.Visibility = Visibility.Collapsed;
                txtFooter.Visibility = Visibility.Visible;
                progressBarFileHash.Value = 0;
            }
            else if (activeTabHeader == "İki Klasör Karşılaştırma")
            {
                if (btnCompareFolders != null)
                {
                    btnCompareFolders.IsEnabled = true;
                }
                progressPanelCompare.Visibility = Visibility.Collapsed;
                progressBarCompare.Value = 0;
            }
            
            // Footer'ı normale döndür
            footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(232, 232, 232)); // Normal gri
        }
        
        // Overlay "Tamam" butonu tıklandığında - DataGrid'i yenile ve overlay'i gizle
        private void OverlayOkButton_Click(object sender, RoutedEventArgs e)
        {
            // Overlay'i gizle
            if (overlayBorder != null)
            {
                overlayBorder.Visibility = Visibility.Collapsed;
            }
            
            // Progress bar'ı göster
            if (overlayProgressBar != null)
            {
                overlayProgressBar.Visibility = Visibility.Visible;
                overlayProgressBar.IsIndeterminate = true;
            }
            
            // Butonu gizle
            if (overlayOkButton != null)
            {
                overlayOkButton.Visibility = Visibility.Collapsed;
            }
            
            // Başlığı sıfırla
            if (overlayTitle != null)
            {
                overlayTitle.Text = "İşlem Yapılıyor...";
            }
            
            // Butonu tekrar enable et - Yeni hesaplama yapılabilir
            if (btnCalculateFileHash != null)
            {
                btnCalculateFileHash.IsEnabled = true;
            }
            
            // Aktif tab'a göre DataGrid'i yenile
            if (activeTabHeader == "Dosya Hash Hesaplama")
            {
                if (dgFileCompareResults != null)
                {
                    dgFileCompareResults.Items.Refresh();
                    dgFileCompareResults.UpdateLayout();
                }
                UpdateFileHashFooter();
            }
            else if (activeTabHeader == "Klasör Hash Hesaplama")
            {
                if (dgResults != null)
                {
                    dgResults.Items.Refresh();
                    dgResults.UpdateLayout();
                    AutoSizeDataGridColumns();
                }
                UpdateFolderHashFooter();
            }
            else if (activeTabHeader == "İki Klasör Karşılaştırma")
            {
                RefreshCompareResultsGrid();
                UpdateCompareHashFooter();
            }
            else if (activeTabHeader == "İki Dosya Karşılaştırma")
            {
                if (dgFileCompareResults != null)
                {
                    dgFileCompareResults.Items.Refresh();
                    dgFileCompareResults.UpdateLayout();
                }
            }
            
            // Tüm UI'ı zorla yenile
            this.UpdateLayout();
            this.InvalidateVisual();
        }
        
        // İki Dosya Karşılaştırma Sekmesi Fonksiyonları
        private void BtnSelectCompareFile1_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                txtCompareFile1.Text = openFileDialog.FileName;
            }
        }

        private void BtnSelectCompareFile2_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                txtCompareFile2.Text = openFileDialog.FileName;
            }
        }

        private async void BtnCompareFiles_Click(object sender, RoutedEventArgs e)
        {
            string file1Path = txtCompareFile1.Text;
            string file2Path = txtCompareFile2.Text;

            if (string.IsNullOrEmpty(file1Path) || !File.Exists(file1Path) ||
                string.IsNullOrEmpty(file2Path) || !File.Exists(file2Path))
            {
                System.Windows.MessageBox.Show("Lütfen karşılaştırmak için iki geçerli dosya seçin.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Dosya bilgilerini al (method başında tanımla - closure için)
                FileInfo fi1 = new FileInfo(file1Path);
                FileInfo fi2 = new FileInfo(file2Path);
                string file1Name = Path.GetFileName(file1Path);
                string file2Name = Path.GetFileName(file2Path);
                long file1Size = fi1.Length;
                long file2Size = fi2.Length;

                // Overlay'i göster
                Dispatcher.Invoke(() =>
                {
                    if (overlayBorder != null)
                    {
                        overlayBorder.Visibility = Visibility.Visible;
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "İşlem Yapılıyor...";
                        }
                        if (overlayProgressText != null)
                        {
                            // file1Name ve file2Name method scope'ta tanımlı, closure ile erişilebilir
                            overlayProgressText.Text = $"📄 {file1Name}\n📄 {file2Name}\nKarşılaştırılıyor...";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Visible;
                            overlayProgressBar.IsIndeterminate = true;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Collapsed;
                        }
                        // İptal butonunu göster
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Visible;
                        }
                    }
                });
                
                fileCompareResults.Clear();
                txtFileCompareStatus.Text = "Karşılaştırma yapılıyor...";
                txtFileCompareStatus.Foreground = System.Windows.Media.Brushes.Black;

                HashInfo hash1 = null;
                HashInfo hash2 = null;

                // İptal token'ı oluştur (eğer yoksa)
                if (cancellationTokenSource == null)
                {
                    cancellationTokenSource = new System.Threading.CancellationTokenSource();
                }
                var cancellationToken = cancellationTokenSource.Token;
                isCancellationRequested = false;

                // İlk dosya hash hesaplama - Progress ile
                await Task.Run(() =>
                {
                    hash1 = CalculateFileHashWithProgress(file1Path, file1Size, cancellationToken, (progress) =>
                    {
                        // İptal kontrolü
                        if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                        {
                            return;
                        }
                        
                        // UI güncellemesi
                        Dispatcher.Invoke(() =>
                        {
                            if (overlayProgressText != null && !cancellationToken.IsCancellationRequested && !isCancellationRequested)
                            {
                                double percentage = Math.Round(progress * 100, 1);
                                overlayProgressText.Text = $"📄 {file1Name}\nHash hesaplanıyor... {percentage}%";
                            }
                        });
                    });
                });
                
                // İptal kontrolü
                if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                {
                    return;
                }
                
                // İkinci dosya hash hesaplama - Progress ile
                await Task.Run(() =>
                {
                    hash2 = CalculateFileHashWithProgress(file2Path, file2Size, cancellationToken, (progress) =>
                    {
                        // İptal kontrolü
                        if (cancellationToken.IsCancellationRequested || isCancellationRequested)
                        {
                            return;
                        }
                        
                        // UI güncellemesi
                        Dispatcher.Invoke(() =>
                        {
                            if (overlayProgressText != null && !cancellationToken.IsCancellationRequested && !isCancellationRequested)
                            {
                                double percentage = Math.Round(progress * 100, 1);
                                overlayProgressText.Text = $"📄 {file2Name}\nHash hesaplanıyor... {percentage}%";
                            }
                        });
                    });
                });

                // Sonuç satırlarını hazırla
                void AddRow(string property, string v1, string v2, bool isSame)
                {
                    fileCompareResults.Add(new FileCompareRow
                    {
                        PropertyName = property,
                        File1Value = v1,
                        File2Value = v2,
                        ComparisonStatus = isSame ? "Aynı" : "Farklı",
                        IsDifferent = !isSame
                    });
                }

                AddRow("Dosya Yolu", file1Path, file2Path,
                    string.Equals(file1Path, file2Path, StringComparison.OrdinalIgnoreCase));

                AddRow("Dosya Boyutu",
                    FormatFileSize(fi1.Length),
                    FormatFileSize(fi2.Length),
                    fi1.Length == fi2.Length);

                if (useMD5)
                {
                    AddRow("MD5", hash1.MD5Hash, hash2.MD5Hash,
                        string.Equals(hash1.MD5Hash, hash2.MD5Hash, StringComparison.OrdinalIgnoreCase));
                }

                if (useSHA1)
                {
                    AddRow("SHA1", hash1.SHA1Hash, hash2.SHA1Hash,
                        string.Equals(hash1.SHA1Hash, hash2.SHA1Hash, StringComparison.OrdinalIgnoreCase));
                }


                // Overlay'de tamamlandı mesajı göster ve butonu göster
                Dispatcher.Invoke(() =>
                {
                    if (overlayBorder != null)
                    {
                        if (overlayTitle != null)
                        {
                            overlayTitle.Text = "✅ İşlem Tamamlandı!";
                        }
                        if (overlayProgressBar != null)
                        {
                            overlayProgressBar.Visibility = Visibility.Collapsed;
                        }
                        if (overlayOkButton != null)
                        {
                            overlayOkButton.Visibility = Visibility.Visible;
                        }
                        // Tamam butonu göründüğünde İptal butonunu gizle
                        if (overlayCancelButton != null)
                        {
                            overlayCancelButton.Visibility = Visibility.Collapsed;
                        }
                    }
                });
                
                // Özet
                bool allSame = fileCompareResults.All(r => !r.IsDifferent);
                string resultMessage = "";
                if (allSame)
                {
                    resultMessage = "Dosyalar aynı.";
                    txtFileCompareStatus.Text = "Sonuç: Dosyalar aynı.";
                    txtFileCompareStatus.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    int diffCount = fileCompareResults.Count(r => r.IsDifferent);
                    resultMessage = $"Dosyalar farklı. Farklı alan sayısı: {diffCount}.";
                    txtFileCompareStatus.Text = $"Sonuç: Dosyalar farklı. Farklı alan sayısı: {diffCount}.";
                    txtFileCompareStatus.Foreground = System.Windows.Media.Brushes.Red;
                }
                
                // Overlay'de sonuç mesajını göster
                Dispatcher.Invoke(() =>
                {
                    if (overlayProgressText != null)
                    {
                        overlayProgressText.Text = $"Karşılaştırma tamamlandı!\n{resultMessage}";
                    }
                });
            }
            catch (Exception ex)
            {
                // Overlay'i gizle
                Dispatcher.Invoke(() =>
                {
                    if (overlayBorder != null)
                    {
                        overlayBorder.Visibility = Visibility.Collapsed;
                    }
                });
                
                System.Windows.MessageBox.Show($"Dosya karşılaştırma sırasında hata oluştu: {ex.Message}", "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtFileCompareStatus.Text = "Karşılaştırma sırasında hata oluştu.";
                txtFileCompareStatus.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        // İki dosya karşılaştırma sonuçlarını PDF olarak çıkar
        private void BtnExportFileCompareToPdf_Click(object sender, RoutedEventArgs e)
        {
            if (fileCompareResults == null || fileCompareResults.Count == 0)
            {
                System.Windows.MessageBox.Show("PDF çıkarmak için önce iki dosyayı karşılaştırmalısınız.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF Dosyaları (*.pdf)|*.pdf",
                FileName = $"Iki_Dosya_Karsilastirma_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
            };

            if (saveFileDialog.ShowDialog() != true)
                return;

            // Overlay göster
            ShowPdfExportOverlay("PDF oluşturuluyor...");
            
            // PDF oluşturmayı async yap
            Task.Run(() =>
            {
                try
                {
                    CreatePdfFromFileCompareResults(fileCompareResults.ToList(), saveFileDialog.FileName);
                    
                    // Overlay'i güncelle
                    Dispatcher.Invoke(() =>
                    {
                        HidePdfExportOverlay("PDF başarıyla oluşturuldu!");
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        HidePdfExportOverlay();
                        System.Windows.MessageBox.Show($"PDF oluşturulurken hata oluştu: {ex.Message}", "Hata",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            });
        }

        // Dosya ve klasör sayısını hesapla - Karşılaştırma için (recursive, erişim hatası kontrolü ile)
        private void CountFilesAndFolders(string folderPath)
        {
            // İptal kontrolü
            if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }
            
            try
            {
                // Bu klasördeki dosyaları say
                string[] files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
                totalFiles += files.Length;
                
                // Bu klasördeki alt klasörleri say
                string[] folders = Directory.GetDirectories(folderPath);
                totalFiles += folders.Length;
                
                // Recursive olarak alt klasörleri de say
                foreach (string subFolder in folders)
                {
                    // İptal kontrolü
                    if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }
                    
                    try
                    {
                        CountFilesAndFolders(subFolder);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Erişim hatası - Bu alt klasörü atla
                    }
                    catch
                    {
                        // Diğer hatalar - Bu alt klasörü atla
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Erişim hatası - Bu klasörü atla
            }
            catch
            {
                // Diğer hatalar - Bu klasörü atla
            }
        }
        
        // Karşılaştırma için klasör işleme - İki klasörü karşılaştırmak için dosyaları ve klasörleri işler
        private void ProcessFolderForComparison(string folderPath, int folderNumber, string basePath, System.Threading.CancellationToken cancellationToken = default)
        {
            // İptal kontrolü
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            
            try
            {
                // Klasör bilgisini ekle - Klasörün kendisini listelemek için
                string folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName))
                {
                    folderName = folderPath;
                }
                
                // Relative path hesapla - Base path parametre olarak geldi, UI elementine erişmeye gerek yok
                string relativeFolderPath = folderPath;
                if (!string.IsNullOrEmpty(basePath) && folderPath.StartsWith(basePath))
                {
                    relativeFolderPath = folderPath.Substring(basePath.Length).TrimStart('\\');
                    if (string.IsNullOrEmpty(relativeFolderPath))
                    {
                        relativeFolderPath = Path.GetFileName(basePath);
                    }
                }
                
                // Klasör hash hesaplama - Cache'den kontrol et, yoksa hesapla ve sakla (tekrar hesaplamayı önler)
                string folderHashMD5 = "";
                string folderHashSHA1 = "";
                
                // Cache'den kontrol et (aynı klasör iki kez işlenmesin)
                lock (folderHashCache)
                {
                    if (folderHashCache.TryGetValue(folderPath, out var cachedHash))
                    {
                        folderHashMD5 = cachedHash.MD5;
                        folderHashSHA1 = cachedHash.SHA1;
                    }
                    else
                    {
                        // Cache'de yok, hesapla ve sakla
                        folderHashMD5 = CalculateFolderHash(folderPath, useMD5, MD5.Create);
                        folderHashSHA1 = CalculateFolderHash(folderPath, useSHA1, SHA1.Create);
                        folderHashCache[folderPath] = (folderHashMD5, folderHashSHA1);
                    }
                }
                
                // Klasör içinde dosya var mı kontrol et (erişim hatası kontrolü ile)
                string[] filesInFolder = null;
                string[] subDirs = null;
                bool isEmpty = true;
                try
                {
                    filesInFolder = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
                    subDirs = Directory.GetDirectories(folderPath);
                    isEmpty = filesInFolder.Length == 0 && subDirs.Length == 0;
                }
                catch (UnauthorizedAccessException)
                {
                    // Erişim hatası - Klasörü boş olarak işaretle
                    isEmpty = true;
                    filesInFolder = new string[0];
                    subDirs = new string[0];
                }
                catch
                {
                    // Diğer hatalar - Klasörü boş olarak işaretle
                    isEmpty = true;
                    filesInFolder = new string[0];
                    subDirs = new string[0];
                }
                
                processedFiles++;
                int folderPercentage = totalFiles > 0 ? (int)((processedFiles * 100.0) / totalFiles) : 0;
                
                uiUpdateCounter++;
                bool shouldUpdateUIFolder = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                
                // Overlay güncellemesi - Daha sık güncelle (klasör hash hesaplamadaki gibi)
                // Overlay için daha küçük batch size kullan (daha sık güncelleme)
                int overlayUpdateBatchSizeFolder = Math.Min(100, UI_UPDATE_BATCH_SIZE);
                bool shouldUpdateOverlayFolder = (uiUpdateCounter % overlayUpdateBatchSizeFolder == 0) || (processedFiles == totalFiles);
                if (shouldUpdateOverlayFolder && isProcessingCompare)
                {
                    // Throttling kontrolü - Saniyede maksimum 2 güncelleme (overlay için daha sık) - klasör hash hesaplamadaki gibi
                    var now = DateTime.Now;
                    int overlayUpdateInterval = 500; // 500ms = saniyede 2 güncelleme (overlay için)
                    bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= overlayUpdateInterval || processedFiles == totalFiles;
                    
                    if (canUpdate)
                    {
                        lastProgressUpdate = now;
                        
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            // Overlay'de klasör yolunu göster (tam yol)
                            if (overlayProgressText != null && !isCancellationRequested && isProcessingCompare)
                            {
                                // Tam yolu göster (relativeFolderPath)
                                string displayPath = relativeFolderPath.Length > 70 ? "..." + relativeFolderPath.Substring(relativeFolderPath.Length - 67) : relativeFolderPath;
                                
                                // Büyük sayılar için formatlama
                                string overlayText = totalFiles > 1000000
                                    ? $"{displayPath}\n{processedFiles:N0}/{totalFiles:N0} %{folderPercentage}"
                                    : $"{displayPath}\n{processedFiles}/{totalFiles} %{folderPercentage}";
                                overlayProgressText.Text = overlayText;
                            }
                            
                            // Footer'da progress güncelle - Throttled
                            if (activeTabHeader == "İki Klasör Karşılaştırma" && isProcessingCompare)
                            {
                                double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                progressBarFileHash.Value = footerProgress;
                                
                                // Büyük sayılar için formatlama
                                string progressText = totalFiles > 1000000 
                                    ? $"{processedFiles:N0}/{totalFiles:N0} dosya ({Math.Round(footerProgress, 1)}%)"
                                    : $"{processedFiles}/{totalFiles} dosya ({Math.Round(footerProgress, 1)}%)";
                                txtProgressFileHash.Text = progressText;
                            }
                        }));
                    }
                }
                
                // Klasör sonucunu topla (UI thread dışında) - Thread-safe
                string folderKey = relativeFolderPath.ToLowerInvariant() + "_folder";
                HashResult folderResult = null;
                
                lock (comparisonDict)
                {
                    if (comparisonDict.ContainsKey(folderKey))
                    {
                        // Klasör zaten var - Karşılaştırma yap
                        var existing = comparisonDict[folderKey];
                        if (folderNumber == 2)
                        {
                            // Hash karşılaştırma - Sadece aktif hash'leri karşılaştır
                            bool allMatch = true;
                            bool hasActiveHash = false;
                            
                            if (useMD5 && existing.MD5Hash != "(Devre dışı)" && folderHashMD5 != "(Devre dışı)")
                            {
                                hasActiveHash = true;
                                if (!existing.MD5Hash.Equals(folderHashMD5, StringComparison.OrdinalIgnoreCase))
                                    allMatch = false;
                            }
                            if (useSHA1 && existing.SHA1Hash != "(Devre dışı)" && folderHashSHA1 != "(Devre dışı)")
                            {
                                hasActiveHash = true;
                                if (!existing.SHA1Hash.Equals(folderHashSHA1, StringComparison.OrdinalIgnoreCase))
                                    allMatch = false;
                            }
                            
                            // Eğer hiç aktif hash yoksa veya tüm aktif hash'ler eşleşiyorsa "Aynı"
                            if (!hasActiveHash || allMatch)
                            {
                                existing.ComparisonStatus = "Aynı";
                                existing.IsDifferent = false;
                            }
                            else
                            {
                                existing.ComparisonStatus = "Farklı";
                                existing.IsDifferent = true;
                            }
                        }
                        folderResult = existing; // Mevcut sonucu kullan
                    }
                    else
                    {
                        // Yeni klasör - İlk klasörden gelen klasör
                        folderResult = new HashResult
                        {
                            FolderPath = relativeFolderPath,
                            FolderName = folderName,
                            FilePath = "",
                            FileName = isEmpty ? $"📁 {folderName} - İçerik yok" : $"📁 {folderName}",
                            FileExtension = "",
                            MD5Hash = folderHashMD5,
                            SHA1Hash = folderHashSHA1,
                            SHA256Hash = "(Devre dışı)",
                            SHA384Hash = "(Devre dışı)",
                            SHA512Hash = "(Devre dışı)",
                            HashDate = DateTime.Now,
                            ComparisonStatus = folderNumber == 1 ? "Bekleniyor" : "",
                            IsDifferent = false,
                            IsFolder = true, // Klasör işareti
                            SourceFolder = folderNumber
                        };
                        comparisonDict[folderKey] = folderResult;
                    }
                }
                
                // Thread-safe ekleme - UI güncellemesi yapma, sadece topla
                if (folderResult != null)
                {
                    lock (pendingCompareResults)
                    {
                        // Eğer zaten eklenmemişse ekle
                        if (!pendingCompareResults.Contains(folderResult))
                        {
                            pendingCompareResults.Add(folderResult);
                        }
                    }
                }
                
                // Dosyaları bul - Sadece bu klasördeki dosyaları işle (recursive alt klasörler ayrı işlenecek)
                string[] files = filesInFolder ?? new string[0];
                foreach (string file in files)
                {
                    try
                    {
                        HashInfo hashInfo = CalculateFileHash(file);
                        string fileName = Path.GetFileName(file);
                        
                        // Relative path hesapla - Base path'e göre (karşılaştırma için kritik!)
                        // Yanlış: file.Replace(folderPath, "") -> Bu sadece mevcut klasörü kaldırır
                        // Doğru: basePath'e göre relative path hesapla
                        string relativePath = "";
                        if (!string.IsNullOrEmpty(basePath) && file.StartsWith(basePath))
                        {
                            relativePath = file.Substring(basePath.Length).TrimStart('\\');
                        }
                        else
                        {
                            // Base path ile eşleşmiyorsa, folderPath'e göre hesapla (fallback)
                            relativePath = file.Replace(folderPath, "").TrimStart('\\');
                        }
                        
                        // Key oluştur - Relative path + folder number (aynı relative path farklı klasörlerde olabilir)
                        // Ancak karşılaştırma için sadece relative path yeterli (aynı yapıda olmalı)
                        string fileKey = relativePath.ToLowerInvariant();

                        processedFiles++;
                        int filePercentage = totalFiles > 0 ? (int)((processedFiles * 100.0) / totalFiles) : 0;

                        // Overlay güncellemesi - Daha sık güncelle (her 100 dosyada bir veya son dosya)
                        uiUpdateCounter++;
                        // Overlay için daha küçük batch size kullan (daha sık güncelleme) - klasör hash hesaplamadaki gibi
                        int overlayUpdateBatchSize = Math.Min(100, UI_UPDATE_BATCH_SIZE);
                        bool shouldUpdateOverlayFile = (uiUpdateCounter % overlayUpdateBatchSize == 0) || (processedFiles == totalFiles);
                        
                        if (shouldUpdateOverlayFile && isProcessingCompare)
                        {
                            // Throttling kontrolü - Saniyede maksimum 2 güncelleme (overlay için daha sık) - klasör hash hesaplamadaki gibi
                            var now = DateTime.Now;
                            int overlayUpdateInterval = 500; // 500ms = saniyede 2 güncelleme (overlay için)
                            bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= overlayUpdateInterval || processedFiles == totalFiles;
                            
                            if (canUpdate)
                            {
                                lastProgressUpdate = now;
                                
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                {
                                    // Overlay'de dosya yolunu göster (tam yol)
                                    if (overlayProgressText != null && !isCancellationRequested && isProcessingCompare)
                                    {
                                        // Tam yolu göster (relativePath)
                                        string displayPath = relativePath.Length > 70 ? "..." + relativePath.Substring(relativePath.Length - 67) : relativePath;
                                        
                                        string overlayText = totalFiles > 1000000
                                            ? $"{displayPath}\n{processedFiles:N0}/{totalFiles:N0} %{filePercentage}"
                                            : $"{displayPath}\n{processedFiles}/{totalFiles} %{filePercentage}";
                                        overlayProgressText.Text = overlayText;
                                    }
                                    
                                    // Footer'da progress güncelle - Throttled (dosya adı ile)
                                    if (activeTabHeader == "İki Klasör Karşılaştırma" && isProcessingCompare)
                                    {
                                        double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                        progressBarFileHash.Value = footerProgress;
                                        
                                        // Dosya adını göster (klasör hash hesaplamadaki gibi)
                                        string displayPath = relativePath.Length > 70 ? "..." + relativePath.Substring(relativePath.Length - 67) : relativePath;
                                        
                                        // Büyük sayılar için formatlama
                                        string progressText = totalFiles > 1000000 
                                            ? $"{processedFiles:N0}/{totalFiles:N0} - {displayPath} ({Math.Round(footerProgress, 1)}%)"
                                            : $"{processedFiles}/{totalFiles} - {displayPath} ({Math.Round(footerProgress, 1)}%)";
                                        txtProgressFileHash.Text = progressText;
                                    }
                                }));
                            }
                        }
                        
                        // Dosya sonucunu topla (UI thread dışında) - Thread-safe
                        HashResult fileResult = null;
                        
                        lock (comparisonDict)
                        {
                            if (comparisonDict.ContainsKey(fileKey))
                            {
                                // Dosya zaten var - Karşılaştırma yap
                                var existing = comparisonDict[fileKey];
                                if (folderNumber == 2)
                                {
                                    // Hash karşılaştırma - Sadece aktif hash'leri karşılaştır
                                    bool allMatch = true;
                                    bool hasActiveHash = false;
                                    
                                    if (useMD5 && existing.MD5Hash != "(Devre dışı)" && hashInfo.MD5Hash != "(Devre dışı)")
                                    {
                                        hasActiveHash = true;
                                        if (!existing.MD5Hash.Equals(hashInfo.MD5Hash, StringComparison.OrdinalIgnoreCase))
                                            allMatch = false;
                                    }
                                    if (useSHA1 && existing.SHA1Hash != "(Devre dışı)" && hashInfo.SHA1Hash != "(Devre dışı)")
                                    {
                                        hasActiveHash = true;
                                        if (!existing.SHA1Hash.Equals(hashInfo.SHA1Hash, StringComparison.OrdinalIgnoreCase))
                                            allMatch = false;
                                    }
                                    
                                    // Eğer hiç aktif hash yoksa veya tüm aktif hash'ler eşleşiyorsa "Aynı"
                                    if (!hasActiveHash || allMatch)
                                    {
                                        existing.ComparisonStatus = "Aynı";
                                        existing.IsDifferent = false;
                                    }
                                    else
                                    {
                                        existing.ComparisonStatus = "Farklı";
                                        existing.IsDifferent = true;
                                        // İkinci klasördeki hash'leri sakla
                                        existing.SecondMD5Hash = hashInfo.MD5Hash;
                                        existing.SecondSHA1Hash = hashInfo.SHA1Hash;
                                        existing.SecondSHA256Hash = hashInfo.SHA256Hash;
                                        existing.SecondSHA384Hash = hashInfo.SHA384Hash;
                                        existing.SecondSHA512Hash = hashInfo.SHA512Hash;
                                        // İkinci klasördeki dosya boyutunu sakla
                                        try
                                        {
                                            if (File.Exists(file))
                                            {
                                                FileInfo fileInfo = new FileInfo(file);
                                                existing.SecondFileSize = fileInfo.Length;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                fileResult = existing; // Mevcut sonucu kullan
                            }
                            else
                            {
                                // Yeni dosya - İlk klasörden gelen dosya
                                long fileSize = 0;
                                try
                                {
                                    if (File.Exists(file))
                                    {
                                        FileInfo fileInfo = new FileInfo(file);
                                        fileSize = fileInfo.Length;
                                    }
                                }
                                catch { }
                                
                                fileResult = new HashResult
                                {
                                    FolderPath = folderPath,
                                    FolderName = Path.GetFileName(folderPath),
                                    FilePath = file,
                                    FileName = fileName,
                                    FileExtension = string.IsNullOrEmpty(Path.GetExtension(file)) ? "(uzantı yok)" : Path.GetExtension(file),
                                    MD5Hash = hashInfo.MD5Hash,
                                    SHA1Hash = hashInfo.SHA1Hash,
                                    SHA256Hash = hashInfo.SHA256Hash,
                                    SHA384Hash = hashInfo.SHA384Hash,
                                    SHA512Hash = hashInfo.SHA512Hash,
                                    FileSize = fileSize,
                                    HashDate = hashInfo.HashDate,
                                    ComparisonStatus = folderNumber == 1 ? "Bekleniyor" : "",
                                    IsDifferent = false,
                                    IsFolder = false, // Dosya
                                    SourceFolder = folderNumber
                                };
                                comparisonDict[fileKey] = fileResult;
                            }
                        }
                        
                        // Thread-safe ekleme - UI güncellemesi yapma, sadece topla
                        if (fileResult != null)
                        {
                            lock (pendingCompareResults)
                            {
                                // Eğer zaten eklenmemişse ekle
                                if (!pendingCompareResults.Contains(fileResult))
                                {
                                    pendingCompareResults.Add(fileResult);
                                }
                            }
                        }
                        
                        // Batch toplama - Belirli aralıklarla allCollectedCompareResults'a aktar
                        if (pendingCompareResults.Count >= UI_ADD_BATCH_SIZE)
                        {
                            lock (pendingCompareResults)
                            {
                                allCollectedCompareResults.AddRange(pendingCompareResults);
                                pendingCompareResults.Clear();
                            }
                        }
                    }
                    catch
                    {
                        // Dosya okuma hatası - Sessizce devam et, sadece progress güncelle
                        processedFiles++;
                        uiUpdateCounter++;
                        
                        // Overlay güncellemesi - Throttled
                        bool shouldUpdateOverlayError = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                        
                        if (shouldUpdateOverlayError && isProcessingCompare)
                        {
                            var now = DateTime.Now;
                            bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= PROGRESS_UPDATE_INTERVAL_MS || processedFiles == totalFiles;
                            
                            if (canUpdate)
                            {
                                lastProgressUpdate = now;
                                
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                {
                                    // Footer'da progress güncelle - Throttled
                                    if (activeTabHeader == "İki Klasör Karşılaştırma" && isProcessingCompare)
                                    {
                                        double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                        progressBarFileHash.Value = footerProgress;
                                        
                                        // Büyük sayılar için formatlama
                                        string progressText = totalFiles > 1000000 
                                            ? $"{processedFiles:N0}/{totalFiles:N0} dosya ({Math.Round(footerProgress, 1)}%)"
                                            : $"{processedFiles}/{totalFiles} dosya ({Math.Round(footerProgress, 1)}%)";
                                        txtProgressFileHash.Text = progressText;
                                    }
                                }));
                            }
                        }
                    }
                }
                
                // Recursive klasör işleme - Alt klasörleri de işler (erişim hatası kontrolü ile)
                string[] subFolders = subDirs ?? new string[0];
                foreach (string subFolder in subFolders)
                {
                    // İptal kontrolü
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    
                    try
                    {
                        ProcessFolderForComparison(subFolder, folderNumber, basePath, cancellationToken);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Erişim hatası - Bu alt klasörü atla ve devam et, sadece progress güncelle
                        processedFiles++;
                        uiUpdateCounter++;
                        
                        // Overlay güncellemesi - Throttled
                        bool shouldUpdateOverlayError = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                        
                        if (shouldUpdateOverlayError && isProcessingCompare)
                        {
                            var now = DateTime.Now;
                            bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= PROGRESS_UPDATE_INTERVAL_MS || processedFiles == totalFiles;
                            
                            if (canUpdate)
                            {
                                lastProgressUpdate = now;
                                
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                {
                                    // Footer'da progress güncelle - Throttled
                                    if (activeTabHeader == "İki Klasör Karşılaştırma" && isProcessingCompare)
                                    {
                                        double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                        progressBarFileHash.Value = footerProgress;
                                        
                                        // Büyük sayılar için formatlama
                                        string progressText = totalFiles > 1000000 
                                            ? $"{processedFiles:N0}/{totalFiles:N0} dosya ({Math.Round(footerProgress, 1)}%)"
                                            : $"{processedFiles}/{totalFiles} dosya ({Math.Round(footerProgress, 1)}%)";
                                        txtProgressFileHash.Text = progressText;
                                    }
                                }));
                            }
                        }
                    }
                    catch
                    {
                        // Diğer hatalar - Bu alt klasörü atla ve devam et, sadece progress güncelle
                        processedFiles++;
                        uiUpdateCounter++;
                        
                        // Overlay güncellemesi - Throttled
                        bool shouldUpdateOverlayError = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                        
                        if (shouldUpdateOverlayError && isProcessingCompare)
                        {
                            var now = DateTime.Now;
                            bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= PROGRESS_UPDATE_INTERVAL_MS || processedFiles == totalFiles;
                            
                            if (canUpdate)
                            {
                                lastProgressUpdate = now;
                                
                                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                {
                                    // Footer'da progress güncelle - Throttled
                                    if (activeTabHeader == "İki Klasör Karşılaştırma" && isProcessingCompare)
                                    {
                                        double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                        progressBarFileHash.Value = footerProgress;
                                        
                                        // Büyük sayılar için formatlama
                                        string progressText = totalFiles > 1000000 
                                            ? $"{processedFiles:N0}/{totalFiles:N0} dosya ({Math.Round(footerProgress, 1)}%)"
                                            : $"{processedFiles}/{totalFiles} dosya ({Math.Round(footerProgress, 1)}%)";
                                        txtProgressFileHash.Text = progressText;
                                    }
                                }));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Klasör erişim hatası işleme - basePath parametre olarak geldi, UI elementine erişmeye gerek yok
                processedFiles++;
                uiUpdateCounter++;
                bool shouldUpdateUIException = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                int errorPercentageException = totalFiles > 0 ? (int)((processedFiles * 100.0) / totalFiles) : 0;
                
                // Relative path hesapla - UI thread dışında yapılabilir
                string folderNameException = Path.GetFileName(folderPath);
                string relativeFolderPath = folderPath;
                if (!string.IsNullOrEmpty(basePath) && folderPath.StartsWith(basePath))
                {
                    relativeFolderPath = folderPath.Substring(basePath.Length).TrimStart('\\');
                    if (string.IsNullOrEmpty(relativeFolderPath))
                    {
                        relativeFolderPath = Path.GetFileName(basePath);
                    }
                }
                
                // Hata sonucunu topla (UI thread dışında) - Thread-safe
                string errorKey = relativeFolderPath.ToLowerInvariant() + "_folder";
                HashResult errorResult = null;
                
                lock (comparisonDict)
                {
                    if (!comparisonDict.ContainsKey(errorKey))
                    {
                        errorResult = new HashResult
                        {
                            FolderPath = relativeFolderPath,
                            FolderName = folderNameException,
                            FilePath = "",
                            FileName = $"📁 {folderNameException}",
                            FileExtension = "",
                            MD5Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                            SHA1Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                            SHA256Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                            SHA384Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                            SHA512Hash = $"KLASÖR ERİŞİM HATASI: {ex.Message}",
                            HashDate = DateTime.Now,
                            ComparisonStatus = folderNumber == 1 ? "Bekleniyor" : "",
                            IsDifferent = false,
                            IsFolder = true, // Klasör işareti
                            SourceFolder = folderNumber
                        };
                        comparisonDict[errorKey] = errorResult;
                    }
                }
                
                // Thread-safe ekleme - UI güncellemesi yapma, sadece topla
                if (errorResult != null)
                {
                    lock (pendingCompareResults)
                    {
                        if (!pendingCompareResults.Contains(errorResult))
                        {
                            pendingCompareResults.Add(errorResult);
                        }
                    }
                }
                
                // Overlay güncellemesi - Throttled
                bool shouldUpdateOverlayException = (uiUpdateCounter % UI_UPDATE_BATCH_SIZE == 0) || (processedFiles == totalFiles);
                
                if (shouldUpdateOverlayException && isProcessingCompare)
                {
                    var now = DateTime.Now;
                    bool canUpdate = (now - lastProgressUpdate).TotalMilliseconds >= PROGRESS_UPDATE_INTERVAL_MS || processedFiles == totalFiles;
                    
                    if (canUpdate)
                    {
                        lastProgressUpdate = now;
                        
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            // Footer'da progress güncelle - Throttled
                            if (activeTabHeader == "İki Klasör Karşılaştırma" && isProcessingCompare)
                            {
                                double footerProgress = totalFiles > 0 ? (processedFiles / (double)totalFiles) * 100.0 : 0;
                                progressBarFileHash.Value = footerProgress;
                                
                                // Büyük sayılar için formatlama
                                string progressText = totalFiles > 1000000 
                                    ? $"{processedFiles:N0}/{totalFiles:N0} dosya ({Math.Round(footerProgress, 1)}%)"
                                    : $"{processedFiles}/{totalFiles} dosya ({Math.Round(footerProgress, 1)}%)";
                                txtProgressFileHash.Text = progressText;
                            }
                        }));
                    }
                }
            }
        }

        // Karşılaştırma sonuçlarını işleme - Beklenen dosyaları işaretler ve sonuçları hazırlar
        private void CompareResults()
        {
            // Beklenen dosyaları işaretle - Hangi klasörde fazla olduğunu belirt
            foreach (var result in compareResults)
            {
                if (result.ComparisonStatus == "Bekleniyor")
                {
                    if (result.SourceFolder == 1)
                    {
                        result.ComparisonStatus = "Klasör 1'de fazla";
                        result.IsDifferent = true;
                    }
                    else if (result.SourceFolder == 2)
                    {
                        result.ComparisonStatus = "Klasör 2'de fazla";
                        result.IsDifferent = true;
                    }
                    else
                    {
                        // Varsayılan olarak fazla kabul et (klasör 1'de fazla)
                        result.ComparisonStatus = "Klasör 1'de fazla";
                        result.IsDifferent = true;
                    }
                }
            }

            // Sonuçları sırala - Fazla olanlar en altta
            var sortedResults = compareResults.OrderBy(r => 
            {
                if (r.ComparisonStatus == "Klasör 1'de fazla" || r.ComparisonStatus == "Klasör 2'de fazla")
                    return 2; // En alta
                else if (r.IsDifferent)
                    return 1; // Ortada
                else
                    return 0; // En üste
            }).ToList();

            // Sonuçları kopyala - Filtreleme için tüm sonuçları sakla
            // ÖNEMLİ: allCompareResults'a tüm sonuçları kopyala (filtreleme için)
            allCompareResults.Clear();
            foreach (var item in sortedResults)
            {
                allCompareResults.Add(item);
            }
            
            // compareResults'ı da sıralı hale getir ve sıra numaralarını set et
            // ÖNEMLİ: compareResults'a da tüm sonuçları ekle (DataGrid için)
            compareResults.Clear();
            int rowNumber = 1;
            foreach (var item in sortedResults)
            {
                item.RowNumber = rowNumber++; // Sıra numarasını burada set et (virtualizasyon sorununu önler)
                compareResults.Add(item);
            }
        }
        
        // Karşılaştırma sonuçlarını tam olarak yenile - DataGrid'i refresh eder
        private void RefreshCompareResultsGrid()
        {
            if (dgCompareResults == null) return;
            
            try
            {
                // DataGrid'i tam olarak yenile - ItemsSource zaten compareResults'a bağlı
                // Sadece refresh yeterli, null yapmaya gerek yok (kaybolma sorununu önler)
                dgCompareResults.Items.Refresh();
                
                // Layout'u güncelle - Virtualizasyon nedeniyle görünmeyen satırlar için
                dgCompareResults.UpdateLayout();
                
                // Column genişliklerini yeniden ayarla
                AutoSizeCompareDataGridColumns();
                
                // UI'ı zorla güncelle
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                
                // ScrollViewer'ı en üste kaydır
                var scrollViewer = FindVisualChild<ScrollViewer>(dgCompareResults);
                if (scrollViewer != null)
                {
                    scrollViewer.ScrollToTop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshCompareResultsGrid error: {ex.Message}");
            }
        }

        // Karşılaştırma sekmesi filtreleme fonksiyonları - ApplyFiltersCompare kullanır
        private void BtnFilterAllCompare_Click(object sender, RoutedEventArgs e)
        {
            currentFilterCompare = "All";
            ApplyFiltersCompare();
        }

        private void BtnFilterSameCompare_Click(object sender, RoutedEventArgs e)
        {
            currentFilterCompare = "Same";
            ApplyFiltersCompare();
        }

        private void BtnFilterDifferentCompare_Click(object sender, RoutedEventArgs e)
        {
            currentFilterCompare = "Different";
            ApplyFiltersCompare();
        }

        private void BtnFilterExtraCompare_Click(object sender, RoutedEventArgs e)
        {
            currentFilterCompare = "Extra";
            ApplyFiltersCompare();
        }

        // Filtreleme uygulama - Karşılaştırma sekmesi için
        private void ApplyFiltersCompare()
        {
            if (allCompareResults == null || allCompareResults.Count == 0)
            {
                compareResults.Clear();
                if (dgCompareResults != null)
                {
                    dgCompareResults.Items.Refresh();
                }
                UpdateCompareHashFooter();
                return;
            }
            
            List<HashResult> filteredResults = new List<HashResult>();
            
            // Filtreleme mantığı - Doğru şekilde
            if (currentFilterCompare == "Same")
            {
                // Aynı olanlar - Sadece ComparisonStatus kontrol et
                filteredResults = allCompareResults.Where(r => r.ComparisonStatus == "Aynı").ToList();
            }
            else if (currentFilterCompare == "Different")
            {
                // Farklı olanlar - ComparisonStatus "Farklı" olanlar
                filteredResults = allCompareResults.Where(r => r.ComparisonStatus == "Farklı").ToList();
            }
            else if (currentFilterCompare == "Extra")
            {
                // Fazla olanlar
                filteredResults = allCompareResults.Where(r => r.ComparisonStatus == "Klasör 1'de fazla" || r.ComparisonStatus == "Klasör 2'de fazla").ToList();
            }
            else
            {
                // Tümü - Filtreleme yok
                filteredResults = allCompareResults.ToList();
            }
            
            // compareResults'ı güncelle - Önce temizle
            compareResults.Clear();
            
            // Filtrelenmiş sonuçları ekle ve sıra numaralarını set et
            int rowNumber = 1;
            foreach (var item in filteredResults)
            {
                item.RowNumber = rowNumber++; // Sıra numarasını set et (virtualizasyon sorununu önler)
                compareResults.Add(item);
            }
            
            // DataGrid'i tam olarak yenile - ItemsSource zaten compareResults'a bağlı
            if (dgCompareResults != null)
            {
                // Sadece refresh yeterli - ItemsSource'u null yapmaya gerek yok (kaybolma sorununu önler)
                dgCompareResults.Items.Refresh();
                dgCompareResults.UpdateLayout();
            }
            
            // Footer'ı güncelle - Her zaman allCompareResults kullan (filtrelenmemiş tüm sonuçlar)
            UpdateCompareHashFooter();
        }

        // DataGrid satır renklendirme - Klasör hash hesaplama sekmesi için
        // Farklı olan dosyaları kırmızı, klasörleri mavi renkte gösterir
        // DataGrid header'ını sticky yap - Scroll edildiğinde header görünür kalır
        private void MakeDataGridHeaderSticky()
        {
            if (dgResults == null) return;
            
            // DataGrid yüklendiğinde header'ı sticky yap
            if (dgResults.IsLoaded)
            {
                ApplyStickyHeader();
            }
            else
            {
                dgResults.Loaded += (s, e) => ApplyStickyHeader();
            }
        }
        
        private void ApplyStickyHeader()
        {
            try
            {
                // DataGrid'in içindeki ScrollViewer'ı bul
                var scrollViewer = FindVisualChild<ScrollViewer>(dgResults);
                if (scrollViewer != null)
                {
                    // DataGrid'in column headers presenter'ını bul
                    var columnHeadersPresenter = FindVisualChild<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>(dgResults);
                    if (columnHeadersPresenter != null)
                    {
                        // Header'ın her zaman görünür olmasını sağla
                        columnHeadersPresenter.Visibility = Visibility.Visible;
                        
                        // Header'ı sticky yapmak için ScrollViewer'ın ScrollChanged event'ini dinle
                        scrollViewer.ScrollChanged += (s, e) =>
                        {
                            // Header'ın her zaman görünür olmasını sağla
                            if (columnHeadersPresenter != null)
                            {
                                columnHeadersPresenter.Visibility = Visibility.Visible;
                                
                                // Header'ın pozisyonunu ayarla - sticky yap
                                var headerPanel = columnHeadersPresenter.Parent as FrameworkElement;
                                if (headerPanel != null)
                                {
                                    // Header'ın her zaman üstte görünür olmasını sağla
                                    Canvas.SetTop(columnHeadersPresenter, 0);
                                }
                            }
                        };
                    }
                }
            }
            catch { }
        }
        
        private void DgResults_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            var result = e.Row.Item as HashResult;
            if (result != null)
            {
                // Sıra numarasını set et (eğer set edilmemişse)
                if (result.RowNumber == 0)
                {
                    int rowIndex = hashResults.IndexOf(result);
                    result.RowNumber = rowIndex + 1;
                }
                
                // Satır yüksekliğini otomatik ayarla (içeriğe göre)
                e.Row.Height = double.NaN; // Auto height
                
                if (result.IsFolder)
                {
                    // Klasörler - Mavi tonunda göster (karşılaştırma sekmesi ile aynı)
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 220, 255));
                    e.Row.FontWeight = FontWeights.SemiBold;
                }
                else if (result.IsDifferent)
                {
                    // Farklı dosyalar - Kırmızı tonunda göster
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 200));
                }
                else
                {
                    // Normal dosyalar - Beyaz
                    e.Row.Background = System.Windows.Media.Brushes.White;
                    e.Row.FontWeight = FontWeights.Normal;
                }
            }
            else
            {
                e.Row.Background = System.Windows.Media.Brushes.White;
                e.Row.Height = double.NaN; // Auto height
            }
        }

        // DataGrid satır renklendirme - Karşılaştırma sekmesi için
        // Farklı olan dosyaları kırmızı renkte gösterir
        private void DgCompareResults_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            var result = e.Row.Item as HashResult;
            if (result != null)
            {
                // Sıra numarasını her zaman DataGrid'deki gerçek index'e göre set et (virtualizasyon için)
                int rowIndex = e.Row.GetIndex();
                result.RowNumber = rowIndex + 1;
                
                // Satır yüksekliğini otomatik ayarla
                e.Row.Height = double.NaN; // Auto height

                // Önce özel durumlar:

                // Fazla olan dosya/klasörler için sarı tonunda arka plan (en altta gösteriliyor)
                if (result.ComparisonStatus == "Klasör 1'de fazla" || result.ComparisonStatus == "Klasör 2'de fazla")
                {
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 200));
                    e.Row.FontWeight = FontWeights.Bold;
                }
                // Bulunamayan dosyalar için turuncu tonunda arka plan
                else if (result.ComparisonStatus == "Bulunamadı")
                {
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 220, 180));
                    e.Row.FontWeight = FontWeights.SemiBold;
                }
                // Klasörler için mavi arka plan
                else if (result.IsFolder)
                {
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 220, 255));
                    e.Row.FontWeight = FontWeights.SemiBold;
                }
                // Aynı dosyalar için yeşil tonunda arka plan
                else if (result.ComparisonStatus == "Aynı")
                {
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 255, 200));
                    e.Row.FontWeight = FontWeights.Normal;
                }
                // Diğer farklı dosyalar için kırmızı tonunda arka plan
                else if (result.IsDifferent)
                {
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 180, 180));
                    e.Row.FontWeight = FontWeights.SemiBold;
                }
                // Normal dosyalar için beyaz arka plan
                else
                {
                    e.Row.Background = System.Windows.Media.Brushes.White;
                    e.Row.FontWeight = FontWeights.Normal;
                }
            }
            else
            {
                e.Row.Background = System.Windows.Media.Brushes.White;
                e.Row.FontWeight = FontWeights.Normal;
            }
        }

        // İki dosya karşılaştırma sonuçları için satır renklendirme
        private void DgFileCompareResults_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            var result = e.Row.Item as FileCompareRow;
            if (result != null)
            {
                result.RowNumber = e.Row.GetIndex() + 1;
                e.Row.Height = double.NaN;

                if (result.IsDifferent)
                {
                    e.Row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 200));
                    e.Row.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    e.Row.Background = System.Windows.Media.Brushes.White;
                    e.Row.FontWeight = FontWeights.Normal;
                }
            }
        }

        // Arama fonksiyonu - Klasör hash hesaplama sekmesi için
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            currentSearchText = txtSearch.Text.ToLowerInvariant();
            ApplyFilters();
        }

        // Arama temizleme - Klasör hash hesaplama sekmesi için
        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            txtSearch.Text = "";
            currentSearchText = "";
            ApplyFilters();
        }

        // Filtreleme sıfırlama - Klasör hash hesaplama sekmesi için
        private void BtnResetFilter_Click(object sender, RoutedEventArgs e)
        {
            // Tüm sonuçları temizle
            hashResults.Clear();
            allHashResults.Clear();
            baseFolderPath = "";
            
            // Arama kutusunu temizle
            txtSearch.Text = "";
            currentSearchText = "";
            
            // Status'u temizle
            txtStatus.Text = "";
            txtStatus.Visibility = Visibility.Collapsed;
            
            // Footer'ı normale döndür
            progressPanelFileHash.Visibility = Visibility.Collapsed;
            txtFooter.Visibility = Visibility.Visible;
            txtFooter.Text = "Hazır";
            footerBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(232, 232, 232));
        }
        
        // Eski BtnResetFilter_Click - Artık kullanılmıyor
        private void BtnResetFilter_Click_OLD(object sender, RoutedEventArgs e)
        {
            txtSearch.Text = "";
            currentSearchText = "";
            ApplyFilters();
        }

        // Filtreleme uygulama - Klasör hash hesaplama sekmesi için (sadece arama)
        private void ApplyFilters()
        {
            hashResults.Clear();
            int rowNumber = 1;
            foreach (var item in allHashResults)
            {
                bool matchesSearch = true;

                // Arama kontrolü
                if (!string.IsNullOrEmpty(currentSearchText))
                {
                    matchesSearch = item.FileName.ToLowerInvariant().Contains(currentSearchText) ||
                                   item.FilePath.ToLowerInvariant().Contains(currentSearchText) ||
                                   item.FolderName.ToLowerInvariant().Contains(currentSearchText) ||
                                   item.FolderPath.ToLowerInvariant().Contains(currentSearchText) ||
                                   item.FileExtension.ToLowerInvariant().Contains(currentSearchText) ||
                                   (!string.IsNullOrEmpty(item.MD5Hash) && item.MD5Hash.ToLowerInvariant().Contains(currentSearchText)) ||
                                   (!string.IsNullOrEmpty(item.SHA1Hash) && item.SHA1Hash.ToLowerInvariant().Contains(currentSearchText));
                }

                if (matchesSearch)
                {
                    item.RowNumber = rowNumber++; // Sıra numarasını set et (virtualizasyon sorununu önler)
                    hashResults.Add(item);
                }
            }
            
            // DataGrid'i tam olarak yenile - Tüm renkler ve durumlar görünsün
            if (dgResults != null)
            {
                dgResults.Items.Refresh();
                dgResults.UpdateLayout();
            }
        }
        
        // Klasör sonuçlarını PDF olarak dışa aktar
        private void BtnExportFolderResultsToPdf_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (hashResults == null || hashResults.Count == 0)
                {
                    System.Windows.MessageBox.Show("Dışa aktarılacak sonuç bulunamadı.", "Uyarı", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Dosya kaydetme dialog'u
                Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PDF Dosyası (*.pdf)|*.pdf",
                    FileName = $"Klasor_Hash_Sonuclari_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                    DefaultExt = "pdf"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    // Overlay göster
                    ShowPdfExportOverlay("PDF oluşturuluyor...");
                    
                    // PDF oluşturmayı async yap
                    Task.Run(() =>
                    {
                        try
                        {
                            // PDF oluştur
                            CreatePdfFromFolderResults(hashResults.ToList(), saveFileDialog.FileName);
                            
                            // Overlay'i güncelle
                            Dispatcher.Invoke(() =>
                            {
                                HidePdfExportOverlay("PDF başarıyla oluşturuldu!");
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                HidePdfExportOverlay();
                                System.Windows.MessageBox.Show($"PDF kaydetme hatası: {ex.Message}", "Hata", 
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"PDF kaydetme hatası: {ex.Message}", "Hata", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        // Klasör sonuçlarını PDF olarak oluştur
        private void CreatePdfFromFolderResults(List<HashResult> results, string filePath)
        {
            try
            {
                // Aktif algoritmaları belirle (başlık ve yön için)
                var activeAlgos = new List<string>();
                if (useMD5) activeAlgos.Add("MD5");
                if (useSHA1) activeAlgos.Add("SHA1");
                string algorithmsHeader = activeAlgos.Count > 0
                    ? "Algoritmalar: " + string.Join(", ", activeAlgos)
                    : "Algoritmalar: (Seçili yok)";

                // PrintDocument kullanarak PDF oluştur
                var printDoc = new PrintDocument();
                
                // PDF yazıcısı bul
                string pdfPrinter = null;
                foreach (string printer in PrinterSettings.InstalledPrinters)
                {
                    if (printer.ToLower().Contains("microsoft print to pdf") || 
                        printer.ToLower().Contains("pdf") ||
                        printer.ToLower().Contains("adobe pdf"))
                    {
                        pdfPrinter = printer;
                        break;
                    }
                }

                if (pdfPrinter == null)
                {
                    System.Windows.MessageBox.Show(
                        "PDF yazıcısı bulunamadı.\n\n" +
                        "PDF oluşturmak için sisteminizde 'Microsoft Print to PDF' yazıcısının yüklü olması gerekir.",
                        "Bilgi",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                printDoc.PrinterSettings.PrinterName = pdfPrinter;
                printDoc.PrinterSettings.PrintToFile = true;
                printDoc.PrinterSettings.PrintFileName = filePath;
                // Klasör hash hesaplama için yatay (Landscape) - Tüm alanların sığması için
                printDoc.DefaultPageSettings.Landscape = true;
                
                // PrintDialog'u gizle - Sessiz yazdırma
                printDoc.PrinterSettings.PrintToFile = true;
                
                int currentPage = 0;
                int itemsPerPage = 20;
                int totalPages = (int)Math.Ceiling((double)results.Count / itemsPerPage);
                
                printDoc.PrintPage += (sender, e) =>
                {
                    var fontFamily = new System.Drawing.FontFamily("Arial");
                    var font = new System.Drawing.Font(fontFamily, 9);
                    var titleFont = new System.Drawing.Font(fontFamily, 18, System.Drawing.FontStyle.Bold);
                    var headerFont = new System.Drawing.Font(fontFamily, 11);
                    var columnHeaderFont = new System.Drawing.Font(fontFamily, 9, System.Drawing.FontStyle.Bold);
                    
                    // Landscape için optimize edilmiş margin'ler
                    // Sol: 20mm, Sağ: 20mm, Üst: 15mm, Alt: 15mm
                    float pageWidth = e.PageBounds.Width;
                    float pageHeight = e.PageBounds.Height;
                    float leftMargin = e.MarginBounds.Left + 15; // 15px ekstra padding
                    float rightMargin = e.MarginBounds.Left + e.MarginBounds.Width - 15; // Sağdan 15px padding
                    float topMargin = e.MarginBounds.Top + 15; // Üstten 15px padding
                    float bottomMargin = e.MarginBounds.Bottom - 15; // Alttan 15px padding (sayfa numarası için)
                    float contentWidth = rightMargin - leftMargin; // Kullanılabilir genişlik
                    float maxYPos = bottomMargin;
                    
                    float yPos = topMargin;
                    float columnWidth = (rightMargin - leftMargin) / 4;
                    
                    // Başlık (ayarlardan)
                    e.Graphics.DrawString(pdfTitle, titleFont, 
                        System.Drawing.Brushes.Black, leftMargin, yPos);
                    yPos += 30;
                    
                    // Dosya Numarası (varsa)
                    if (!string.IsNullOrWhiteSpace(pdfFileNumber))
                    {
                        e.Graphics.DrawString(pdfFileNumber, headerFont, 
                            System.Drawing.Brushes.Gray, leftMargin, yPos);
                        yPos += 18;
                    }
                    
                    // Kurum (varsa)
                    if (!string.IsNullOrWhiteSpace(pdfOrganization))
                    {
                        e.Graphics.DrawString(pdfOrganization, headerFont, 
                            System.Drawing.Brushes.Gray, leftMargin, yPos);
                        yPos += 18;
                    }

                    // Seçili algoritmalar
                    e.Graphics.DrawString(algorithmsHeader, headerFont,
                        System.Drawing.Brushes.Gray, leftMargin, yPos);
                    yPos += 18;
                    
                    yPos += 7;
                    
                    // Çizgi - Tam genişlikte
                    e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                        leftMargin, yPos, rightMargin, yPos);
                    yPos += 15;
                    
                    // Toplam kayıt sayısı
                    e.Graphics.DrawString($"Toplam {results.Count} kayıt - Sayfa {currentPage + 1}/{totalPages}", 
                                font, System.Drawing.Brushes.Black, leftMargin, yPos);
                    yPos += 20;
                    
                    // Kolon başlıkları
                    float xPos = leftMargin;
                    float totalWidth = rightMargin - leftMargin;
                    float col0Width = totalWidth * 0.08f; // Sıra No %8
                    float col1Width, col2Width, col3Width;
                    
                    if (pdfShowDateTime)
                    {
                        col1Width = totalWidth * 0.32f; // Dosya/Klasör Adı %32
                        col2Width = totalWidth * 0.50f; // Hash Değerleri %50
                        col3Width = totalWidth * 0.10f; // Tarih %10
                    }
                    else
                    {
                        col1Width = totalWidth * 0.35f; // Dosya/Klasör Adı %35
                        col2Width = totalWidth * 0.57f; // Hash Değerleri %57
                        col3Width = 0; // Tarih yok
                    }
                    
                        e.Graphics.DrawString("Sıra", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += col0Width;
                    e.Graphics.DrawString("Dosya/Klasör Adı", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += col1Width;
                    e.Graphics.DrawString("Hash Değerleri", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += col2Width;
                    if (pdfShowDateTime)
                    {
                        e.Graphics.DrawString("Tarih", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                    }
                    yPos += 15;
                    
                    // Çizgi - Tam genişlikte (kolon başlıkları altı)
                    e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                        leftMargin, yPos, rightMargin, yPos);
                    yPos += 10;
                    
                    // Veriler
                    int startIndex = currentPage * itemsPerPage;
                    int endIndex = Math.Min(startIndex + itemsPerPage, results.Count);
                    
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        var item = results[i];
                        xPos = leftMargin;
                        
                        // Sayfa numaraları için yer kontrolü - Eğer yeterli yer yoksa bir sonraki sayfaya geç
                        if (yPos > maxYPos - 50) // 50 pixel güvenlik payı
                        {
                            break; // Bu sayfaya daha fazla satır sığmaz
                        }
                        
                        // Sıra No
                        int rowNumber = i + 1;
                        e.Graphics.DrawString(rowNumber.ToString(), font, System.Drawing.Brushes.Black, xPos, yPos);
                        xPos += col0Width;
                        
                        // Dosya/Klasör Adı - Alt satıra geçebilir
                        string fileName = !string.IsNullOrEmpty(item.FileName) ? item.FileName : item.FolderName;
                        if (string.IsNullOrEmpty(fileName))
                        {
                            fileName = !string.IsNullOrEmpty(item.FilePath) ? Path.GetFileName(item.FilePath) : "";
                        }
                        if (string.IsNullOrEmpty(fileName))
                        {
                            fileName = item.FolderPath;
                        }
                        // Klasör ise farklı belirt (PDF'de düzgün görünsün)
                        if (item.IsFolder)
                        {
                            // Emoji karakterlerini kaldır (PDF'de düzgün render edilmiyor)
                            fileName = fileName.Replace("📁", "").Trim();
                            if (string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(item.FolderName))
                            {
                                fileName = $"[KLASÖR] {item.FolderName}";
                            }
                            else if (!string.IsNullOrEmpty(item.FolderName))
                            {
                                // Klasör adını al, emoji olmadan
                                string folderName = item.FolderName;
                                if (!fileName.Contains(folderName))
                                {
                                    fileName = $"[KLASÖR] {folderName}";
                                }
                                else
                                {
                                    fileName = $"[KLASÖR] {fileName.Replace("📁", "").Trim()}";
                                }
                            }
                            else
                            {
                                fileName = $"[KLASÖR] {fileName}";
                            }
                        }
                        
                        RectangleF fileNameRect = new RectangleF(xPos, yPos, col1Width - 5, maxYPos - yPos);
                        StringFormat fileNameFormat = new StringFormat();
                        fileNameFormat.Alignment = StringAlignment.Near;
                        fileNameFormat.LineAlignment = StringAlignment.Near;
                        fileNameFormat.FormatFlags = StringFormatFlags.NoClip; // Sarmalama için
                        fileNameFormat.Trimming = StringTrimming.Word; // Kelime bazında kesme
                        // Metni çiz (otomatik sarmalama)
                        e.Graphics.DrawString(fileName, font, System.Drawing.Brushes.Black, fileNameRect, fileNameFormat);
                        // Gerçek yüksekliği ölç
                        SizeF fileNameSize = e.Graphics.MeasureString(fileName, font, new SizeF(col1Width - 5, maxYPos - yPos), fileNameFormat);
                        float fileNameHeight = fileNameSize.Height;
                        
                        xPos += col1Width;
                        
                        // Hash değerleri - tüm aktif algoritmalar bir arada, alt satırlara geçebilir
                        StringBuilder hashBuilder = new StringBuilder();
                        if (useMD5 && !string.IsNullOrEmpty(item.MD5Hash) && item.MD5Hash != "(Devre dışı)")
                        {
                            hashBuilder.AppendLine("MD5: " + item.MD5Hash);
                        }
                        if (useSHA1 && !string.IsNullOrEmpty(item.SHA1Hash) && item.SHA1Hash != "(Devre dışı)")
                        {
                            hashBuilder.AppendLine("SHA1: " + item.SHA1Hash);
                        }
                        
                        string hashText = hashBuilder.ToString().TrimEnd();
                        RectangleF hashRect = new RectangleF(xPos, yPos, col2Width - 5, maxYPos - yPos);
                        StringFormat hashFormat = new StringFormat();
                        hashFormat.Alignment = StringAlignment.Near;
                        hashFormat.LineAlignment = StringAlignment.Near;
                        hashFormat.FormatFlags = StringFormatFlags.NoClip;
                        hashFormat.Trimming = StringTrimming.Word;
                        e.Graphics.DrawString(hashText, font, System.Drawing.Brushes.Black, hashRect, hashFormat);
                        SizeF hashSize = e.Graphics.MeasureString(hashText, font, new SizeF(col2Width - 5, maxYPos - yPos), hashFormat);
                        float hashHeight = hashSize.Height;
                        
                        xPos += col2Width;
                        
                        // Tarih - kendi hücresinde, sınırlar içinde (sadece ayar açıksa)
                        float dateHeight = 0;
                        if (pdfShowDateTime)
                        {
                            string dateStr = item.HashDate.ToString("yyyy-MM-dd\nHH:mm");
                            RectangleF dateRect = new RectangleF(xPos, yPos, col3Width - 5, maxYPos - yPos);
                            StringFormat dateFormat = new StringFormat();
                            dateFormat.Alignment = StringAlignment.Near;
                            dateFormat.LineAlignment = StringAlignment.Near;
                            dateFormat.FormatFlags = StringFormatFlags.NoClip;
                            dateFormat.Trimming = StringTrimming.Word;
                            e.Graphics.DrawString(dateStr, font, System.Drawing.Brushes.Black, dateRect, dateFormat);
                            SizeF dateSize = e.Graphics.MeasureString(dateStr, font, new SizeF(col3Width - 5, maxYPos - yPos), dateFormat);
                            dateHeight = dateSize.Height;
                        }
                        
                        // En yüksek satır yüksekliğini al
                        float maxHeight = Math.Max(fileNameHeight, hashHeight);
                        if (pdfShowDateTime)
                        {
                            maxHeight = Math.Max(maxHeight, dateHeight);
                        }
                        yPos += maxHeight + 8; // Daha fazla boşluk
                        
                        // Her satırın altına ince çizgi (satır bitiminden sonra)
                        // Satır ayırıcı çizgi - Tam genişlikte
                        e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.LightGray, 0.5f), 
                            leftMargin, yPos - 4, rightMargin, yPos - 4);
                    }
                    
                    // Sayfa numarasını alt kısımda göster
                    string pageNumberText = $"Sayfa {currentPage + 1} / {totalPages}";
                    SizeF pageNumberSize = e.Graphics.MeasureString(pageNumberText, font);
                    float pageNumberX = (pageWidth - pageNumberSize.Width) / 2;
                    float pageNumberY = pageHeight - 25;
                    e.Graphics.DrawString(pageNumberText, font, System.Drawing.Brushes.Black, pageNumberX, pageNumberY);
                    
                    currentPage++;
                    e.HasMorePages = (currentPage < totalPages);
                };
                
                // Sessiz yazdırma - PrintDialog gösterme
                Task.Run(() =>
                {
                    try
                    {
                        printDoc.Print();
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            HidePdfExportOverlay();
                            System.Windows.MessageBox.Show($"PDF yazdırma hatası: {ex.Message}", "Hata", 
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    HidePdfExportOverlay();
                    System.Windows.MessageBox.Show($"PDF oluşturma hatası: {ex.Message}", "Hata", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        // Arama fonksiyonu - Karşılaştırma sekmesi için (şu anda kullanılmıyor)
        // Not: XAML'de txtSearchCompare kontrolü yok, bu yüzden bu fonksiyonlar devre dışı
        /*
        private void TxtSearchCompare_TextChanged(object sender, TextChangedEventArgs e)
        {
            currentSearchTextCompare = txtSearchCompare.Text.ToLowerInvariant();
            ApplyFiltersCompare();
        }

        private void BtnClearSearchCompare_Click(object sender, RoutedEventArgs e)
        {
            txtSearchCompare.Text = "";
            currentSearchTextCompare = "";
            ApplyFiltersCompare();
        }

        private void BtnResetFilterCompare_Click(object sender, RoutedEventArgs e)
        {
            txtSearchCompare.Text = "";
            currentSearchTextCompare = "";
            currentFilterCompare = "All";
            ApplyFiltersCompare();
        }
        */

        // Karşılaştırma sonuçlarını PDF olarak çıkar
        private void BtnExportCompareToPdf_Click(object sender, RoutedEventArgs e)
        {
            if (compareResults == null || compareResults.Count == 0)
            {
                System.Windows.MessageBox.Show("PDF çıkarmak için önce karşılaştırma yapmalısınız.", "Uyarı", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Filtreye göre sonuçları al
            List<HashResult> resultsToExport = new List<HashResult>();
            
            if (currentFilterCompare == "Same")
            {
                // Aynı olanlar
                resultsToExport = compareResults.Where(r => !r.IsDifferent && r.ComparisonStatus == "Aynı").ToList();
            }
            else if (currentFilterCompare == "Different")
            {
                // Farklı olanlar
                resultsToExport = compareResults.Where(r => r.IsDifferent && r.ComparisonStatus == "Farklı").ToList();
            }
            else if (currentFilterCompare == "Extra")
            {
                // Fazla olanlar
                resultsToExport = compareResults.Where(r => r.ComparisonStatus == "Klasör 1'de fazla" || r.ComparisonStatus == "Klasör 2'de fazla").ToList();
            }
            else
            {
                // Tümü
                resultsToExport = compareResults.ToList();
            }
            
            if (resultsToExport.Count == 0)
            {
                System.Windows.MessageBox.Show("Seçilen filtreye göre çıkarılacak sonuç yok.", "Bilgi", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            // Dosya kaydetme dialog'u
            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog();
            saveFileDialog.Filter = "PDF Dosyaları (*.pdf)|*.pdf";
            saveFileDialog.FileName = $"Karşılaştırma_Sonuclari_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            
            if (saveFileDialog.ShowDialog() == true)
            {
                // Overlay göster
                ShowPdfExportOverlay("PDF oluşturuluyor...");
                
                // PDF oluşturmayı async yap
                Task.Run(() =>
                {
                    try
                    {
                        CreatePdfFromCompareResults(resultsToExport, saveFileDialog.FileName);
                        
                        // Overlay'i güncelle
                        Dispatcher.Invoke(() =>
                        {
                            HidePdfExportOverlay("PDF başarıyla oluşturuldu!");
                        });
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            HidePdfExportOverlay();
                            System.Windows.MessageBox.Show($"PDF oluşturulurken hata oluştu: {ex.Message}", "Hata", 
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            }
        }
        
        // Karşılaştırma sonuçlarından PDF oluştur
        private void CreatePdfFromCompareResults(List<HashResult> results, string filePath)
        {
            PrintDocument printDoc = new PrintDocument();
            
            // Aktif algoritmaları belirle (başlık ve yön için)
            var activeAlgos = new List<string>();
            if (useMD5) activeAlgos.Add("MD5");
            if (useSHA1) activeAlgos.Add("SHA1");
            string algorithmsHeader = activeAlgos.Count > 0
                ? "Algoritmalar: " + string.Join(", ", activeAlgos)
                : "Algoritmalar: (Seçili yok)";
            
            // PDF yazıcısı bul
            string pdfPrinter = null;
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                if (printer.ToLower().Contains("microsoft print to pdf") || 
                    printer.ToLower().Contains("pdf") ||
                    printer.ToLower().Contains("adobe pdf"))
                {
                    pdfPrinter = printer;
                    break;
                }
            }
            
            if (pdfPrinter == null)
            {
                System.Windows.MessageBox.Show(
                    "PDF yazıcısı bulunamadı.\n\n" +
                    "PDF oluşturmak için sisteminizde 'Microsoft Print to PDF' yazıcısının yüklü olması gerekir.",
                    "Bilgi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            
            printDoc.PrinterSettings.PrinterName = pdfPrinter;
            printDoc.PrinterSettings.PrintToFile = true;
            printDoc.PrinterSettings.PrintFileName = filePath;
            // İki klasör karşılaştırma için yatay (Landscape) - Tüm alanların sığması için
            printDoc.DefaultPageSettings.Landscape = true;
            
            int currentPage = 0;
            int itemsPerPage = 20;
            int totalPages = (int)Math.Ceiling((double)results.Count / itemsPerPage);
            
            printDoc.PrintPage += (sender, e) =>
            {
                var fontFamily = new System.Drawing.FontFamily("Arial");
                var font = new System.Drawing.Font(fontFamily, 9);
                var titleFont = new System.Drawing.Font(fontFamily, 18, System.Drawing.FontStyle.Bold);
                var headerFont = new System.Drawing.Font(fontFamily, 11);
                var columnHeaderFont = new System.Drawing.Font(fontFamily, 9, System.Drawing.FontStyle.Bold);
                
                // Landscape için optimize edilmiş margin'ler
                float pageWidth = e.PageBounds.Width;
                float pageHeight = e.PageBounds.Height;
                float leftMargin = e.MarginBounds.Left + 15; // 15px ekstra padding
                float rightMargin = e.MarginBounds.Left + e.MarginBounds.Width - 15; // Sağdan 15px padding
                float topMargin = e.MarginBounds.Top + 15; // Üstten 15px padding
                float bottomMargin = e.MarginBounds.Bottom - 15; // Alttan 15px padding
                float contentWidth = rightMargin - leftMargin; // Kullanılabilir genişlik
                float maxYPos = bottomMargin;
                float yPos = topMargin;
                
                // Başlık (ayarlardan)
                e.Graphics.DrawString(pdfTitle, titleFont, 
                    System.Drawing.Brushes.Black, leftMargin, yPos);
                yPos += 30;
                
                // Dosya Numarası (varsa)
                if (!string.IsNullOrWhiteSpace(pdfFileNumber))
                {
                    e.Graphics.DrawString(pdfFileNumber, headerFont, 
                        System.Drawing.Brushes.Gray, leftMargin, yPos);
                    yPos += 18;
                }
                
                // Kurum (varsa)
                if (!string.IsNullOrWhiteSpace(pdfOrganization))
                {
                    e.Graphics.DrawString(pdfOrganization, headerFont, 
                        System.Drawing.Brushes.Gray, leftMargin, yPos);
                    yPos += 18;
                }

                // Seçili algoritmalar
                e.Graphics.DrawString(algorithmsHeader, headerFont,
                        System.Drawing.Brushes.Gray, leftMargin, yPos);
                yPos += 18;
                
                yPos += 7;
                
                    // Çizgi - Tam genişlikte
                    e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                        leftMargin, yPos, rightMargin, yPos);
                yPos += 15;
                
                // Filtre bilgisi
                string filterInfo;
                if (currentFilterCompare == "Same")
                    filterInfo = "Aynı Olanlar";
                else if (currentFilterCompare == "Different")
                    filterInfo = "Farklı Olanlar";
                else if (currentFilterCompare == "Extra")
                    filterInfo = "Fazla Olanlar";
                else
                    filterInfo = "Tümü";
                e.Graphics.DrawString($"Filtre: {filterInfo} - Toplam {results.Count} kayıt - Sayfa {currentPage + 1}/{totalPages}", 
                                font, System.Drawing.Brushes.Black, leftMargin, yPos);
                yPos += 20;
                
                // Kolon başlıkları
                float xPos = leftMargin;
                float availableWidth = rightMargin - leftMargin;

                // Sabit kolonlar
                float col0Width = availableWidth * 0.05f; // Sıra
                float col1Width = availableWidth * 0.20f; // Dosya Adı
                float col2Width = availableWidth * 0.08f; // Uzantı
                float col5Width = availableWidth * 0.13f; // Durum
                float col6Width = pdfShowDateTime ? availableWidth * 0.10f : 0; // Tarih (sadece ayar açıksa)

                // Aktif hash algoritmaları listesi (sırayla)
                var activeAlgorithms = new List<string>();
                if (useMD5) activeAlgorithms.Add("MD5");
                if (useSHA1) activeAlgorithms.Add("SHA1");

                int algoCount = activeAlgorithms.Count;
                float remainingWidth = availableWidth - (col0Width + col1Width + col2Width + col5Width + col6Width);
                if (remainingWidth < 0) remainingWidth = 0;
                float perAlgoWidth = algoCount > 0 ? remainingWidth / algoCount : 0f;

                // Başlık satırı
                        e.Graphics.DrawString("Sıra", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col0Width;
                    e.Graphics.DrawString("Dosya Adı", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col1Width;
                    e.Graphics.DrawString("Uzantı", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col2Width;

                // Her aktif algoritma için ayrı kolon başlığı
                foreach (var algo in activeAlgorithms)
                {
                    e.Graphics.DrawString(algo, columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += perAlgoWidth;
                }

                e.Graphics.DrawString("Durum", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col5Width;
                if (pdfShowDateTime)
                {
                    e.Graphics.DrawString("Tarih", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                }
                yPos += 15;
                
                    // Çizgi - Tam genişlikte
                    e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                        leftMargin, yPos, rightMargin, yPos);
                yPos += 10;
                
                // Veriler
                int startIndex = currentPage * itemsPerPage;
                int endIndex = Math.Min(startIndex + itemsPerPage, results.Count);
                
                for (int i = startIndex; i < endIndex; i++)
                {
                    var item = results[i];
                    xPos = leftMargin;
                    
                    // Sayfa numaraları için yer kontrolü - Eğer yeterli yer yoksa bir sonraki sayfaya geç
                    if (yPos > maxYPos - 50) // 50 pixel güvenlik payı
                    {
                        break; // Bu sayfaya daha fazla satır sığmaz
                    }
                    
                    // Sıra No
                    int rowNumber = i + 1;
                    e.Graphics.DrawString(rowNumber.ToString(), font, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += col0Width;
                    
                    // Dosya/Klasör Adı - Alt satıra geçebilir
                    string fileName = !string.IsNullOrEmpty(item.FileName) ? item.FileName : "";
                    if (item.IsFolder)
                    {
                        // Klasör ise farklı belirt (PDF'de düzgün görünsün)
                        fileName = fileName.Replace("📁", "").Trim();
                        if (string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(item.FolderName))
                        {
                            fileName = $"[KLASÖR] {item.FolderName}";
                        }
                        else if (!string.IsNullOrEmpty(item.FolderName))
                        {
                            string folderName = item.FolderName;
                            if (!fileName.Contains(folderName))
                            {
                                fileName = $"[KLASÖR] {folderName}";
                            }
                            else
                            {
                                fileName = $"[KLASÖR] {fileName.Replace("📁", "").Trim()}";
                            }
                        }
                        else
                        {
                            fileName = $"[KLASÖR] {fileName}";
                        }
                    }
                    RectangleF fileNameRect = new RectangleF(xPos, yPos, col1Width - 5, maxYPos - yPos);
                    StringFormat fileNameFormat = new StringFormat();
                    fileNameFormat.Alignment = StringAlignment.Near;
                    fileNameFormat.LineAlignment = StringAlignment.Near;
                    fileNameFormat.FormatFlags = StringFormatFlags.NoClip;
                    fileNameFormat.Trimming = StringTrimming.Word;
                    e.Graphics.DrawString(fileName, font, System.Drawing.Brushes.Black, fileNameRect, fileNameFormat);
                    SizeF fileNameSize = e.Graphics.MeasureString(fileName, font, new SizeF(col1Width - 5, maxYPos - yPos), fileNameFormat);
                    float fileNameHeight = fileNameSize.Height;
                    xPos += col1Width;
                    
                    // Uzantı
                    e.Graphics.DrawString(item.FileExtension ?? "", font, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += col2Width;
                    
                    // Tüm aktif algoritmalar için kolonlar
                    float maxHashHeight = 0f;
                    foreach (var algo in activeAlgorithms)
                    {
                        string hashValue = "";
                        switch (algo)
                        {
                            case "MD5":
                                hashValue = item.MD5Hash ?? "";
                                break;
                            case "SHA1":
                                hashValue = item.SHA1Hash ?? "";
                                break;
                        }
                        
                        RectangleF algoRect = new RectangleF(xPos, yPos, perAlgoWidth - 5, maxYPos - yPos);
                        StringFormat algoFormat = new StringFormat();
                        algoFormat.FormatFlags = StringFormatFlags.NoClip;
                        algoFormat.Trimming = StringTrimming.Word;
                        e.Graphics.DrawString(hashValue, font, System.Drawing.Brushes.Black, algoRect, algoFormat);
                        SizeF algoSize = e.Graphics.MeasureString(hashValue, font, new SizeF(perAlgoWidth - 5, maxYPos - yPos), algoFormat);
                        if (algoSize.Height > maxHashHeight)
                            maxHashHeight = algoSize.Height;
                        
                        xPos += perAlgoWidth;
                    }
                    
                    // Durum (fazla olan gibi uzun metinler için sütun içinde sarmalama)
                    string status = item.ComparisonStatus ?? "";
                    System.Drawing.Brush statusBrush = item.IsDifferent ? System.Drawing.Brushes.Red : System.Drawing.Brushes.Green;
                    RectangleF statusRect = new RectangleF(xPos, yPos, col5Width - 5, maxYPos - yPos);
                    StringFormat statusFormat = new StringFormat();
                    statusFormat.Alignment = StringAlignment.Near;
                    statusFormat.LineAlignment = StringAlignment.Near;
                    statusFormat.FormatFlags = StringFormatFlags.NoClip;
                    statusFormat.Trimming = StringTrimming.Word;
                    e.Graphics.DrawString(status, font, statusBrush, statusRect, statusFormat);
                    SizeF statusSize = e.Graphics.MeasureString(status, font, new SizeF(col5Width - 5, maxYPos - yPos), statusFormat);
                    float statusHeight = statusSize.Height;
                    xPos += col5Width;
                    
                    // Tarih (kendi sütununda) - sadece ayar açıksa
                    float dateHeight = 0;
                    if (pdfShowDateTime)
                    {
                        string dateStr = item.HashDate.ToString("yyyy-MM-dd HH:mm:ss");
                        RectangleF dateRect = new RectangleF(xPos, yPos, col6Width - 5, maxYPos - yPos);
                        StringFormat dateFormat = new StringFormat();
                        dateFormat.Alignment = StringAlignment.Near;
                        dateFormat.LineAlignment = StringAlignment.Near;
                        dateFormat.FormatFlags = StringFormatFlags.NoClip;
                        dateFormat.Trimming = StringTrimming.Word;
                        e.Graphics.DrawString(dateStr, font, System.Drawing.Brushes.Black, dateRect, dateFormat);
                        SizeF dateSize = e.Graphics.MeasureString(dateStr, font, new SizeF(col6Width - 5, maxYPos - yPos), dateFormat);
                        dateHeight = dateSize.Height;
                    }
                    
                    // Satır yüksekliğini hesapla (tüm sütun yüksekliklerini dikkate al)
                    float rowHeight = Math.Max(fileNameHeight, 18);
                    rowHeight = Math.Max(rowHeight, maxHashHeight);
                    rowHeight = Math.Max(rowHeight, statusHeight);
                    if (pdfShowDateTime)
                    {
                        rowHeight = Math.Max(rowHeight, dateHeight);
                    }
                    
                    yPos += rowHeight + 5;
                }
                
                // Sayfa numarasını alt kısımda göster
                string pageNumberText = $"Sayfa {currentPage + 1} / {totalPages}";
                SizeF pageNumberSize = e.Graphics.MeasureString(pageNumberText, font);
                float pageNumberX = (pageWidth - pageNumberSize.Width) / 2;
                float pageNumberY = pageHeight - 25;
                e.Graphics.DrawString(pageNumberText, font, System.Drawing.Brushes.Black, pageNumberX, pageNumberY);
                
                currentPage++;
                e.HasMorePages = currentPage < totalPages;
            };
            
            printDoc.Print();
        }

        // İki dosya karşılaştırma sonuçlarından PDF oluştur
        private void CreatePdfFromFileCompareResults(List<FileCompareRow> results, string filePath)
        {
            PrintDocument printDoc = new PrintDocument();

            // PDF yazıcısı bul
            string pdfPrinter = null;
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                if (printer.ToLower().Contains("microsoft print to pdf") ||
                    printer.ToLower().Contains("pdf") ||
                    printer.ToLower().Contains("adobe pdf"))
                {
                    pdfPrinter = printer;
                    break;
                }
            }

            if (pdfPrinter == null)
            {
                System.Windows.MessageBox.Show(
                    "PDF yazıcısı bulunamadı.\n\n" +
                    "PDF oluşturmak için sisteminizde 'Microsoft Print to PDF' yazıcısının yüklü olması gerekir.",
                    "Bilgi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            printDoc.PrinterSettings.PrinterName = pdfPrinter;
            printDoc.PrinterSettings.PrintToFile = true;
            printDoc.PrinterSettings.PrintFileName = filePath;
            // İki dosya karşılaştırma için yatay (Landscape) - Tüm alanların sığması için
            printDoc.DefaultPageSettings.Landscape = true;
            
            // Sessiz yazdırma
            printDoc.PrinterSettings.PrintToFile = true;

            int currentPage = 0;
            int itemsPerPage = 25;
            int totalPages = (int)Math.Ceiling((double)results.Count / itemsPerPage);

            printDoc.PrintPage += (sender, e) =>
            {
                var fontFamily = new System.Drawing.FontFamily("Arial");
                var font = new System.Drawing.Font(fontFamily, 9);
                var titleFont = new System.Drawing.Font(fontFamily, 18, System.Drawing.FontStyle.Bold);
                var headerFont = new System.Drawing.Font(fontFamily, 11);
                var columnHeaderFont = new System.Drawing.Font(fontFamily, 9, System.Drawing.FontStyle.Bold);

                // Landscape için optimize edilmiş margin'ler
                float pageWidth = e.PageBounds.Width;
                float pageHeight = e.PageBounds.Height;
                float leftMargin = e.MarginBounds.Left + 15; // 15px ekstra padding
                float rightMargin = e.MarginBounds.Left + e.MarginBounds.Width - 15; // Sağdan 15px padding
                float topMargin = e.MarginBounds.Top + 15; // Üstten 15px padding
                float bottomMargin = e.MarginBounds.Bottom - 15; // Alttan 15px padding
                float contentWidth = rightMargin - leftMargin; // Kullanılabilir genişlik
                float maxYPos = bottomMargin;
                float yPos = topMargin;

                // Başlık (ayarlardan)
                e.Graphics.DrawString(pdfTitle, titleFont,
                    System.Drawing.Brushes.Black, leftMargin, yPos);
                yPos += 30;

                // Dosya Numarası (varsa)
                if (!string.IsNullOrWhiteSpace(pdfFileNumber))
                {
                    e.Graphics.DrawString(pdfFileNumber, headerFont,
                        System.Drawing.Brushes.Gray, leftMargin, yPos);
                    yPos += 18;
                }

                // Kurum (varsa)
                if (!string.IsNullOrWhiteSpace(pdfOrganization))
                {
                    e.Graphics.DrawString(pdfOrganization, headerFont,
                        System.Drawing.Brushes.Gray, leftMargin, yPos);
                    yPos += 18;
                }

                yPos += 7;

                    // Çizgi - Tam genişlikte
                    e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1.5f), 
                        leftMargin, yPos, rightMargin, yPos);
                yPos += 15;

                e.Graphics.DrawString($"İki Dosya Karşılaştırma Sonuçları - Toplam {results.Count} satır - Sayfa {currentPage + 1}/{totalPages}",
                                font, System.Drawing.Brushes.Black, leftMargin, yPos);
                yPos += 20;

                // Kolon başlıkları
                float xPos = leftMargin;
                float availableWidth = rightMargin - leftMargin;
                float col0Width = availableWidth * 0.06f; // Sıra
                float col1Width = availableWidth * 0.20f; // Özellik
                float col2Width = availableWidth * 0.32f; // Dosya 1
                float col3Width = availableWidth * 0.32f; // Dosya 2
                float col4Width = availableWidth * 0.10f; // Durum

                        e.Graphics.DrawString("Sıra", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col0Width;
                e.Graphics.DrawString("Özellik", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col1Width;
                e.Graphics.DrawString("Dosya 1", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col2Width;
                e.Graphics.DrawString("Dosya 2", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                xPos += col3Width;
                e.Graphics.DrawString("Durum", columnHeaderFont, System.Drawing.Brushes.Black, xPos, yPos);
                yPos += 15;

                e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.Black, 1),
                    leftMargin, yPos, rightMargin, yPos);
                yPos += 10;

                int startIndex = currentPage * itemsPerPage;
                int endIndex = Math.Min(startIndex + itemsPerPage, results.Count);

                for (int i = startIndex; i < endIndex; i++)
                {
                    var row = results[i];
                    
                    // Sayfa numaraları için yer kontrolü - Eğer yeterli yer yoksa bir sonraki sayfaya geç
                    if (yPos > maxYPos - 50) // 50 pixel güvenlik payı
                    {
                        break; // Bu sayfaya daha fazla satır sığmaz
                    }

                    float rowStartY = yPos;
                    xPos = leftMargin;

                    // Sıra
                    string rowNum = (i + 1).ToString();
                    e.Graphics.DrawString(rowNum, font, System.Drawing.Brushes.Black, xPos, yPos);
                    xPos += col0Width;

                    // Özellik
                    RectangleF propRect = new RectangleF(xPos, yPos, col1Width - 4, maxYPos - yPos);
                    var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, FormatFlags = StringFormatFlags.NoClip, Trimming = StringTrimming.Word };
                    e.Graphics.DrawString(row.PropertyName ?? "", font, System.Drawing.Brushes.Black, propRect, sf);
                    SizeF propSize = e.Graphics.MeasureString(row.PropertyName ?? "", font, new SizeF(col1Width - 4, maxYPos - yPos), sf);
                    float propHeight = propSize.Height;
                    xPos += col1Width;

                    // Dosya 1 değeri
                    RectangleF f1Rect = new RectangleF(xPos, yPos, col2Width - 4, maxYPos - yPos);
                    e.Graphics.DrawString(row.File1Value ?? "", font, System.Drawing.Brushes.Black, f1Rect, sf);
                    SizeF f1Size = e.Graphics.MeasureString(row.File1Value ?? "", font, new SizeF(col2Width - 4, maxYPos - yPos), sf);
                    float f1Height = f1Size.Height;
                    xPos += col2Width;

                    // Dosya 2 değeri
                    RectangleF f2Rect = new RectangleF(xPos, yPos, col3Width - 4, maxYPos - yPos);
                    e.Graphics.DrawString(row.File2Value ?? "", font, System.Drawing.Brushes.Black, f2Rect, sf);
                    SizeF f2Size = e.Graphics.MeasureString(row.File2Value ?? "", font, new SizeF(col3Width - 4, maxYPos - yPos), sf);
                    float f2Height = f2Size.Height;
                    xPos += col3Width;

                    // Durum
                    System.Drawing.Brush statusBrush = row.IsDifferent ? System.Drawing.Brushes.Red : System.Drawing.Brushes.Green;
                    e.Graphics.DrawString(row.ComparisonStatus ?? "", font, statusBrush, xPos, yPos);

                    float rowHeight = Math.Max(Math.Max(propHeight, f1Height), Math.Max(f2Height, 18));
                    yPos = rowStartY + rowHeight + 5;

                    e.Graphics.DrawLine(new System.Drawing.Pen(System.Drawing.Brushes.LightGray, 0.5f),
                        leftMargin, yPos - 3, rightMargin, yPos - 3);
                }

                // Sayfa numarasını alt kısımda göster
                string pageNumberText = $"Sayfa {currentPage + 1} / {totalPages}";
                SizeF pageNumberSize = e.Graphics.MeasureString(pageNumberText, font);
                float pageNumberX = (pageWidth - pageNumberSize.Width) / 2;
                float pageNumberY = pageHeight - 25;
                e.Graphics.DrawString(pageNumberText, font, System.Drawing.Brushes.Black, pageNumberX, pageNumberY);

                currentPage++;
                e.HasMorePages = currentPage < totalPages;
            };

            printDoc.Print();
        }
        
        // Karşılaştırma DataGrid kolonlarını otomatik boyutlandır
        private void AutoSizeCompareDataGridColumns()
        {
            if (dgCompareResults == null || dgCompareResults.Columns.Count == 0)
                return;
            
            // Eğer henüz yüklenmemişse, yükleme tamamlanana kadar bekle
            if (!dgCompareResults.IsLoaded)
                return;
            
            // Sadece aktif tab için çalış (performans için)
            TabItem selectedTab = mainTabControl?.SelectedItem as TabItem;
            if (selectedTab != null)
            {
                string tabHeader = selectedTab.Header?.ToString() ?? "";
                if (tabHeader != "İki Klasör Karşılaştırma")
                    return;
            }
            
            try
            {
                // DataGrid'in gerçek genişliğini al (scrollbar hariç)
                double availableWidth = dgCompareResults.ActualWidth;
                if (availableWidth <= 0)
                {
                    return; // ActualWidth hazır değilse bekleme, tekrar çağrılacak
                }
                
                // Scrollbar genişliği için rezervasyon (yaklaşık 20px)
                double scrollbarWidth = 0;
                var scrollViewer = FindVisualChild<ScrollViewer>(dgCompareResults);
                if (scrollViewer != null && scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    scrollbarWidth = 20;
                }
                availableWidth -= scrollbarWidth;
                
                // Sabit genişlikli kolonlar
                double fixedWidth = 0;
                fixedWidth += 60; // Sıra
                fixedWidth += 80; // Uzantı
                fixedWidth += 100; // Durum
                fixedWidth += 150; // Tarih
                
                // Kalan genişliği hesapla
                double remainingWidth = availableWidth - fixedWidth;
                if (remainingWidth <= 0)
                    return;
                
                // Otomatik genişlikli kolon sayısını hesapla
                int autoSizeColumns = 0;
                if (colCompareMD5 != null && colCompareMD5.Visibility == Visibility.Visible) autoSizeColumns++;
                if (colCompareSHA1 != null && colCompareSHA1.Visibility == Visibility.Visible) autoSizeColumns++;
                if (colCompareSHA256 != null && colCompareSHA256.Visibility == Visibility.Visible) autoSizeColumns++;
                if (colCompareSHA384 != null && colCompareSHA384.Visibility == Visibility.Visible) autoSizeColumns++;
                if (colCompareSHA512 != null && colCompareSHA512.Visibility == Visibility.Visible) autoSizeColumns++;
                
                // Dosya Adı kolonu (her zaman görünür)
                autoSizeColumns += 1;
                
                if (autoSizeColumns == 0)
                    return;
                
                // Her otomatik kolon için genişlik hesapla - Eşit şekilde dağıt
                double autoColumnWidth = remainingWidth / autoSizeColumns;
                
                // Minimum genişlik kontrolü - Daha esnek
                double minAutoWidth = 80; // Daha küçük minimum genişlik
                if (autoColumnWidth < minAutoWidth)
                {
                    // Eğer minimum genişlikten küçükse, eşit dağıtım yap
                    autoColumnWidth = Math.Max(minAutoWidth, remainingWidth / Math.Max(1, autoSizeColumns));
                }
                
                // Kolonları güncelle - Eşit şekilde dağıt
                foreach (var column in dgCompareResults.Columns)
                {
                    string header = column.Header?.ToString() ?? "";
                    
                    if (header == "Sıra")
                    {
                        column.Width = 60;
                    }
                    else if (header == "Uzantı")
                    {
                        column.Width = 80;
                    }
                    else if (header == "Durum")
                    {
                        column.Width = 100;
                    }
                    else if (header == "Tarih")
                    {
                        column.Width = 150;
                    }
                    else if (header == "Dosya Adı")
                    {
                        // Dosya adı için eşit genişlik - Text wrapping ile alan yetmiyorsa alta insin
                        column.Width = Math.Max(autoColumnWidth, 100); // Minimum 100
                    }
                    else if (column == colCompareMD5 || column == colCompareSHA1 || column == colCompareSHA256 || 
                             column == colCompareSHA384 || column == colCompareSHA512)
                    {
                        if (column.Visibility == Visibility.Visible)
                        {
                            // Hash kolonları için eşit genişlik - Text wrapping ile alan yetmiyorsa alta insin
                            column.Width = Math.Max(autoColumnWidth, 80); // Minimum 80
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda sessizce devam et
                System.Diagnostics.Debug.WriteLine($"AutoSizeCompareDataGridColumns error: {ex.Message}");
            }
        }
        
        // Karşılaştırma DataGrid yüklendiğinde
        private void DgCompareResults_Loaded(object sender, RoutedEventArgs e)
        {
            // Performans için gecikme ile
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (dgCompareResults != null && dgCompareResults.IsLoaded)
                {
                    AutoSizeCompareDataGridColumns();
                }
            }), DispatcherPriority.Background);
        }
        
        // Karşılaştırma DataGrid boyutu değiştiğinde
        private void DgCompareResults_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (compareResizeTimer != null)
            {
                compareResizeTimer.Stop();
                compareResizeTimer.Start();
            }
        }
        

        // Yardım butonu - Kullanıcıya yardım bilgilerini gösterir
        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            // Özel Yardım penceresini göster (logo + açıklama metni)
            HelpWindow helpWindow = new HelpWindow();
            helpWindow.Owner = this;
            helpWindow.ShowDialog();
        }
    }

    // Hash sonuçlarını tutan sınıf - Dosya bilgileri ve hash değerlerini içerir
    public class HashResult
    {
        public int RowNumber { get; set; } // Sıra numarası
        public string FolderPath { get; set; }
        public string FolderName { get; set; }
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileExtension { get; set; }
        public string MD5Hash { get; set; }
        public string SHA1Hash { get; set; }
        public string SHA256Hash { get; set; }
        public string SHA384Hash { get; set; }
        public string SHA512Hash { get; set; }
        // İkinci klasördeki hash'ler (karşılaştırma için)
        public string SecondMD5Hash { get; set; }
        public string SecondSHA1Hash { get; set; }
        public string SecondSHA256Hash { get; set; }
        public string SecondSHA384Hash { get; set; }
        public string SecondSHA512Hash { get; set; }
        public long FileSize { get; set; } // Dosya boyutu (bytes) - Klasör 1
        public long SecondFileSize { get; set; } // Dosya boyutu (bytes) - Klasör 2
        public DateTime HashDate { get; set; }
        public string ComparisonStatus { get; set; } // "Aynı", "Farklı", "Bekleniyor", "Bulunamadı", "Klasör 1'de fazla", "Klasör 2'de fazla"
        public bool IsDifferent { get; set; }
        public bool IsFolder { get; set; } // Klasör mü dosya mı?
        public int SourceFolder { get; set; } // Hangi klasörden geldi (1 veya 2)
    }
    
    // İki dosya karşılaştırma satırı
    public class FileCompareRow
    {
        public int RowNumber { get; set; }
        public string PropertyName { get; set; }
        public string File1Value { get; set; }
        public string File2Value { get; set; }
        public string ComparisonStatus { get; set; } // "Aynı" / "Farklı"
        public bool IsDifferent { get; set; }
    }
    
    // Dosya listesi için sınıf
    public class FileItem : INotifyPropertyChanged
    {
        private string _label;
        private string _filePath;
        private bool _isDuplicate;
        private System.Windows.Media.Brush _backgroundColor;
        private bool _canRemove = true; // X butonu görünürlüğü için
        
        public string Label
        {
            get { return _label; }
            set
            {
                _label = value;
                OnPropertyChanged();
            }
        }
        
        public string FilePath
        {
            get { return _filePath; }
            set
            {
                _filePath = value;
                OnPropertyChanged();
            }
        }
        
        public bool IsDuplicate
        {
            get { return _isDuplicate; }
            set
            {
                _isDuplicate = value;
                OnPropertyChanged();
            }
        }
        
        public System.Windows.Media.Brush BackgroundColor
        {
            get { return _backgroundColor ?? System.Windows.Media.Brushes.Transparent; }
            set
            {
                _backgroundColor = value;
                OnPropertyChanged();
            }
        }
        
        public bool CanRemove
        {
            get { return _canRemove; }
            set
            {
                _canRemove = value;
                OnPropertyChanged();
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    // Hash bilgilerini tutan sınıf - Tüm hash türlerini içerir
    public class HashInfo
    {
        public string MD5Hash { get; set; } = "";
        public string SHA1Hash { get; set; } = "";
        public string SHA256Hash { get; set; } = "";
        public string SHA384Hash { get; set; } = "";
        public string SHA512Hash { get; set; } = "";
        public DateTime HashDate { get; set; }
    }
    
    // Hash sonuçları için sınıf
    public class HashResultItem : INotifyPropertyChanged
    {
        private string _fileName;
        private string _filePath;
        private string _hashResult;
        private HashInfo _hashInfo; // Orijinal hash bilgilerini sakla
        private DateTime _hashDate; // Tarih bilgisini sakla
        
        public string FileName
        {
            get { return _fileName; }
            set
            {
                _fileName = value;
                OnPropertyChanged();
            }
        }
        
        public string FilePath
        {
            get { return _filePath; }
            set
            {
                _filePath = value;
                OnPropertyChanged();
            }
        }
        
        // Orijinal hash bilgileri
        public HashInfo HashInfo
        {
            get { return _hashInfo; }
            set
            {
                _hashInfo = value;
                if (value != null)
                {
                    _hashDate = value.HashDate;
                }
                UpdateHashResult();
            }
        }
        
        public DateTime HashDate
        {
            get { return _hashDate; }
            set
            {
                _hashDate = value;
                UpdateHashResult();
            }
        }
        
        // Dinamik olarak oluşturulan hash sonucu
        public string HashResult
        {
            get { return _hashResult; }
            private set
            {
                _hashResult = value;
                OnPropertyChanged();
            }
        }
        
        // Hash sonucunu güncelle (ayarlara göre)
        public void UpdateHashResult(bool useMD5, bool useSHA1, bool showDateTime)
        {
            if (_hashInfo == null) return;
            
            StringBuilder sb = new StringBuilder();
            
            // Dosya adını ekle
            if (!string.IsNullOrEmpty(_fileName))
            {
                sb.AppendLine($"Dosya: {_fileName}");
                sb.AppendLine();
            }
            
            // Sadece seçili ve hesaplanmış hash'leri göster
            if (useMD5 && _hashInfo.MD5Hash != "(Devre dışı)")
                sb.AppendLine($"MD5: {_hashInfo.MD5Hash}");
            if (useSHA1 && _hashInfo.SHA1Hash != "(Devre dışı)")
                sb.AppendLine($"SHA1: {_hashInfo.SHA1Hash}");
            
            if (showDateTime)
            {
                sb.AppendLine($"Tarih: {_hashDate:yyyy-MM-dd HH:mm:ss}");
            }
            
            HashResult = sb.ToString();
        }
        
        private void UpdateHashResult()
        {
            // Bu method sadece HashInfo veya HashDate değiştiğinde çağrılır
            // Ama ayarlar henüz bilinmediği için burada bir şey yapmıyoruz
            // UpdateHashResult(bool, bool, bool) methodu kullanılmalı
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Yardımcı: dosya boyutunu okunabilir formata çevirme
    public partial class MainWindow
    {
        private string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";

            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }
    
    // File Size Converter - Dosya boyutunu okunabilir formata çevirir
    public class FileSizeConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null || !(value is long))
                return "";
            
            long bytes = (long)value;
            if (bytes == 0)
                return "";
            
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            
            return $"{len:0.##} {sizes[order]}";
        }
        
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    // Hash Comparison Converter - Farklı olanlar için hash ve dosya boyutunu gösterir
    public class HashComparisonConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values == null || values.Length < 6)
                return "";
            
            string hash1 = values[0]?.ToString() ?? "";
            string hash2 = values[1]?.ToString() ?? "";
            long fileSize1 = values[2] is long ? (long)values[2] : 0;
            long fileSize2 = values[3] is long ? (long)values[3] : 0;
            bool isDifferent = values[4] is bool ? (bool)values[4] : false;
            bool isFolder = values[5] is bool ? (bool)values[5] : false;
            
            // Klasör veya aynı ise sadece hash göster
            if (isFolder || !isDifferent)
            {
                return hash1;
            }
            
            // Farklı ise: Klasör 1: hash1 (boyut1) / Klasör 2: hash2 (boyut2)
            string size1 = FormatFileSize(fileSize1);
            string size2 = FormatFileSize(fileSize2);
            
            return $"Klasör 1: {hash1} ({size1})\nKlasör 2: {hash2} ({size2})";
        }
        
        private string FormatFileSize(long bytes)
        {
            if (bytes == 0)
                return "0 B";
            
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            
            return $"{len:0.##} {sizes[order]}";
        }
        
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    // Width Converter - Dosya yolu ve dosya adı için MaxWidth hesaplamak için
    public class WidthConverter : System.Windows.Data.IValueConverter
    {
        public static WidthConverter Instance = new WidthConverter();
        
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double width && width > 0)
            {
                // ScrollViewer genişliğinden, butonlar (75+35+5+8), padding (10*2+15*2) çıkar (hash results için)
                // Veya Label (90+12), butonlar (90+40+8+8), padding (12*2+10*2) çıkar (file list için)
                // Hash results için: 75+35+5+8+10+10+15+15+20 = 193
                // File list için: 90+12+90+40+8+8+12+12+10+10+20 = 312
                // Daha küçük değeri kullan (hash results)
                double maxWidth = width - 75 - 35 - 5 - 8 - 10 - 10 - 15 - 15 - 20;
                return Math.Max(150, maxWidth); // Minimum 150px
            }
            return double.NaN; // Sınırsız
        }
        
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
