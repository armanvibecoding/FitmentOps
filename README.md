# AutoPartsStore - Yedek Parça E-Ticaret Platformu

[![TR](https://img.shields.io/badge/lang-TR-red.svg)](#turkish) [![EN](https://img.shields.io/badge/lang-EN-blue.svg)](#english)
[![Status](https://img.shields.io/badge/Status-Development-orange.svg)]()
[![Security](https://img.shields.io/badge/Security-Baseline-blue.svg)]()
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4.svg)]()
[![React](https://img.shields.io/badge/React-19-61DAFB.svg)]()
[![License](https://img.shields.io/badge/License-Educational-yellow.svg)]()

---

<a name="turkish"></a>
## 🇹🇷 Türkçe

ASP.NET Core 9 Web API ve React 19 ile geliştirilen otomobil yedek parça e-ticaret prototipi.

> **Durum**: Proje aktif geliştirme aşamasındadır ve henüz production ortamına hazır değildir. Sunucu fiyatlı checkout, hosted-payment rezervasyon koordinasyonu, ödeme/iade durum makineleri, parçalı sevkiyat, RMA, B2B fiyat/RFQ/tedarikçi çekirdeği, pazaryeri fail-closed sınırı, kanıtlı araç uyumluluğu, Garajım/bakım günlüğü, rol ayrımlı admin ve operasyonel sağlık kontrolleri vardır. Gerçek online ödeme, kargo, e-belge ve pazaryeri adaptörleri bilerek kapalıdır; sağlayıcı hesapları, hukuki/KVKK kararları, sandbox sertifikasyonu ve gerçek SQL Server kapıları tamamlanmadan gerçek satışa açılmamalıdır.

### 🎯 Projenin Öne Çıkan Yanları

- **Güvenli geliştirme tabanı**: CI, frontend lint/build ve backend regresyon testleri
- **Kapsamlı Validasyon**: 8 entity modelinde Data Annotations ile tam validasyon
- **Kontrollü Ticaret Çekirdeği**: İdempotent checkout, monotonik ödeme/iade geçişleri ve append-only sağlayıcı olay kaydı
- **Güvenli Yapılandırma**: Environment variables, JWT secret yönetimi
- **Hata Yönetimi**: Global Exception Handler + Error Boundary
- **API Dokümantasyonu**: RESTful API ile tutarlı endpoint yapısı

### ✨ Özellikler

#### Müşteri Özellikleri
- **Ürün Katalog Sistemi**: Kategoriler ve markalar bazında filtreleme
- **Gelişmiş Arama**: Ürün adı, marka ve parça numarasına göre arama
- **Sepet Yönetimi**: Gerçek zamanlı sepet güncellemeleri
- **Favori Listesi**: Beğenilen ürünleri kaydetme ve yönetme (localStorage ile kalıcı)
- **Kullanıcı Profili**: Profil bilgileri ve sipariş istatistikleri
- **Sipariş Geçmişi**: Tüm siparişlerin detaylı görüntülenmesi
- **Ürün İncelemeleri**: Yıldız bazlı değerlendirme ve yorum sistemi
- **Araç Uyumluluğu**: Marka → model → nesil → motor → konfigürasyon seçimiyle yalnız doğrulanmış katalog kanıtından `Exact`/`Compatible` sonucu; kanıt yoksa güvenli `Unknown`
- **Parça Kodu Bulma**: Doğrulanmış OEM, üretici ve interchange kodlarının normalize edilmiş araması
- **Garajım ve Bakım Günlüğü**: Birden fazla katalog aracı, kilometre, bakım kalemleri, tarih/km hatırlatıcıları ve geçmiş parçaya tekrar erişim
- **Sürümlü Yasal Kabul**: Yayındaki zorunlu ön bilgilendirme ve mesafeli satış metinleri yüklenmeden veya tam kabul edilmeden checkout fail-closed kalır
- **Fitment Güveni**: Eşik altındaki kaydı olumlu göstermeyen güven puanı/bandı ve kayıtlı garaj aracıyla otomatik ürün kontrolü
- **Kurumsal Merkez**: Bayi başvurusu, toplu RFQ, teklif görüntüleme ve kabul akışı
- **Bildirim Sistemi**: Toast bildirimleri ile kullanıcı geri bildirimi
- **Sayfalama**: Performanslı ürün listeleme (12 ürün/sayfa)
- **404 Sayfası**: Özel hata sayfası
- **Error Boundary**: React hata yakalama mekanizması

#### Admin Özellikleri
- **Dashboard**: Profesyonel istatistikler ve genel bakış
- **Ürün Yönetimi**: CRUD işlemleri (Ekle, Düzenle, Sil)
- **Sipariş Yönetimi**: Sipariş durumu güncellemeleri
- **Kategori Yönetimi**: Kategori oluşturma ve düzenleme
- **Marka Yönetimi**: Araç ve parça markalarını yönetme
- **Stok Kontrol**: Otomatik stok takibi
- **Email Bildirimleri**: Sipariş onayları için otomatik email
- **Ödeme Operasyonları**: Ödeme listesi, teslimatta tahsilat, brüt/iade/net finans özeti
- **Fulfillment Operasyonları**: Kısmi/çoklu sevkiyat, takip bilgisi ve kontrollü sevkiyat durum komutları
- **RMA Operasyonları**: Teslim edilmiş siparişten adet kontrollü iade talebi ve inceleme yaşam döngüsü
- **Entegrasyon Hazırlığı**: Ödeme, e-belge, SMTP, outbox, rezervasyon süpürücüsü, public origin ve kargo için credential göstermeyen fail-closed yetenek görünümü
- **Yasal Metin Yönetimi**: SuperAdmin için değiştirilemez taslak, tek aktif sürüm, yayınlama/emekliye ayırma, optimistic concurrency ve audit intent
- **Fitment ve Kod Yönetimi**: Araç ağacı, ürün–araç uyumu, OEM/interchange kodu, kaynak/provenance ve geçerlilik yönetimi
- **Audit Görünümü**: Append-only SHA-256 zincirli yönetim olaylarını listeleme ve zincir bütünlüğünü doğrulama
- **B2B ve Tedarikçi Yönetimi**: Müşteri grubu, fiyat listesi/kuralı, bayi onayı, RFQ, tedarikçi teklifi ve kaynak seçimi
- **Pazaryeri Kontrolü**: Trendyol/Hepsiburada kanal yeteneği, listing fiyat/stok sapması ve inbox görünümü; adapter yoksa etkinleştirme engeli
- **Garaj Operasyonu**: Destek rolü için kişisel bakım notlarını açığa çıkarmayan salt-okunur özet ve kullanıcı-ID araması

#### Teknik Özellikler
- **JWT Authentication**: Güvenli kimlik doğrulama
- **Role-Based Authorization**: Admin ve kullanıcı rolleri
- **Responsive Design**: Mobil uyumlu arayüz
- **Context API**: Global state yönetimi
- **RESTful API**: Standart API yapısı
- **Entity Framework Core**: ORM ve veritabanı yönetimi
- **Global Exception Handler**: Merkezi hata yönetimi
- **Model Validations**: Tüm modellerde Data Annotations
- **Environment Variables**: Güvenli konfigürasyon yönetimi
- **Hosted ödeme sınırı**: Kart/PAN/CVV modeli yok; yapılandırılmamış online ödeme fail-closed çalışır
- **Dayanıklı işlem temeli**: PaymentAttempt, PaymentTransaction, Refund, PaymentEvent ve bounded outbox
- **Satış sonrası çekirdeği**: Shipment/ShipmentItem ve ReturnRequest/ReturnItem modelleri; idempotency, concurrency ve quantity sınırları
- **Türkiye e-belge sınırı**: Sağlayıcıdan bağımsız immutable tutar/vergi snapshot sözleşmeleri; yapılandırılmadığında sahte başarı üretmeyen gateway
- **Fitment veri güvenliği**: Provider bağımsız araç ağacı, doğrulanmış kaynak zorunluluğu, benzersiz natural key/source kayıtları ve tarihsel geçerlilik
- **Yetki ayrımı**: Finance, Warehouse, Catalog, Support ve SuperAdmin endpoint politikaları; mevcut `Admin` rolü için geçiş uyumluluğu
- **Audit ve gözlemlenebilirlik**: Ham istek/payload saklamayan hash zinciri, güvenli correlation middleware’i, `/health/live` ve `/health/ready`
- **Stok rezervasyonu**: Hosted checkout’a bağlı serializable reserve/release/expire/commit, son stok ve geçiş yarış testleri
- **SEO temeli**: Ürün canonical/JSON-LD, HTTPS origin kontrollü sitemap indexi, 50 bin URL’lik ürün sitemap bölme ve fail-closed robots
- **Web güvenliği**: Exact-origin CORS doğrulaması, HSTS ve production güvenlik başlıkları; wildcard veya credential içeren origin kabul edilmez
- **Kabul kanıtı**: Siparişle aynı transaction’da belge türü/sürümü/SHA-256 snapshot’ı; ham idempotency anahtarı, IP veya user-agent saklanmaz

### 🏗️ Teknoloji ve Mimari Kararlar

#### Backend Mimarisi
- **Entity Framework Core**: Code-First yaklaşımı ile veritabanı yönetimi
- **Controller + Service sınırları**: Kritik checkout, sipariş, ödeme, iade ve outbox davranışı servislerde
- **DTO Pattern**: Admin ve dış API yanıtlarında kontrollü veri transfer nesneleri
- **Dependency Injection**: .NET Core built-in DI container
- **Middleware Pipeline**: Global hata yakalama ve authentication

#### Frontend Mimarisi
- **Context API**: Global state management (Auth, Cart, Wishlist, Notification)
- **Component-Based**: Reusable ve modüler component yapısı
- **Custom Hooks**: useAuth, useCart, useWishlist, useNotification
- **Axios Interceptors**: Otomatik token ekleme ve hata yönetimi
- **Error Boundary**: React error catching pattern

#### Güvenlik Stratejisi
- **BCrypt**: Password hashing (cost factor: 10)
- **JWT**: Stateless authentication (24 saat geçerlilik)
- **Data Annotations**: Model seviyesinde input validasyonu
- **Environment Variables**: Hassas bilgilerin güvenli saklanması
- **CORS Policy**: Origin kontrolü
- **Rate limiting**: Kimlik doğrulama ve misafir sipariş takibi için IP bazlı sınır
- **Gizli veri koruması**: Varsayılan admin parolası yok; provider token/kimlik/idempotency alanları genel JSON dışında

### 🛠 Teknoloji Yığını

#### Backend
- ASP.NET Core 9.0 Web API
- Entity Framework Core 9.0
- SQL Server / SQL Server LocalDB
- JWT Authentication
- AutoMapper
- BCrypt.Net (Şifre hash)
- MailKit (Email servisi)

#### Frontend
- React 19
- React Router v8.3 (patched non-RSC imports from `react-router`)
- Axios
- Context API (Auth, Cart, Wishlist, Notification)
- CSS3 (Custom styling)

### 📦 Kurulum

#### Gereksinimler
- .NET 9.0 SDK
- Node.js 22.22 veya üzeri
- SQL Server veya SQL Server LocalDB
- Git

#### Backend Kurulumu

1. Repository'yi klonlayın:
```bash
git clone <repository-url>
cd AutoPartsStore/Backend/AutoPartsStore.API
```

2. Bağlantı dizesini ayarlayın:
`appsettings.json` dosyasında SQL Server bağlantı dizesini güncelleyin:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=AutoPartsDb;Trusted_Connection=true;TrustServerCertificate=true"
  }
}
```

3. **ÖNEMLİ**: JWT Secret Key'i ayarlayın:
JWT anahtarı repoya yazılmaz. Geliştirme ve production için en az 32 karakterlik environment variable kullanın:
```bash
export Jwt__Key="replace-with-a-random-secret-at-least-32-characters"
```

PowerShell:
```powershell
$env:Jwt__Key="replace-with-a-random-secret-at-least-32-characters"
```

Production originlerini de ortamdan verin; varsayılan public site origin’i boştur ve bu durumda sitemap `503`, robots ise `Disallow: /` döner:
```powershell
$env:Cors__AllowedOrigins__0="https://shop.example.com"
$env:PublicSite__BaseUrl="https://shop.example.com"
```

Checkout varsayılan olarak `PreliminaryInformation` ve `DistanceSalesAgreement` türlerinde iki yayınlanmış belge ister. Migration sonrasında SuperAdmin, **Yasal metinler** sekmesinde hukuk tarafından onaylanan her metni ayrı bir sürüm olarak oluşturup yayınlamalıdır. Metinlerden biri yoksa `GET /api/legal/checkout-documents` ve yeni checkout girişimleri `503` ile fail-closed kalır. Mevcut yayınlanmış içerik düzenlenmez; değişiklik yeni sürümle yapılır.

4. Veritabanını oluşturun:
```bash
dotnet ef database update
```

5. Uygulamayı çalıştırın:
```bash
dotnet run
```

Backend API `http://localhost:5167` adresinde çalışacaktır.

#### Frontend Kurulumu

1. Frontend klasörüne gidin:
```bash
cd ../../Frontend/client
```

2. Environment dosyasını oluşturun:
`.env` dosyası mevcuttur. İçeriği:
```env
VITE_API_BASE_URL=http://localhost:5167/api
```

3. Kilitli bağımlılıkları yükleyin:
```bash
npm ci
```

4. Geliştirme sunucusunu başlatın:
```bash
npm run dev
```

Frontend uygulaması `http://localhost:5173` adresinde çalışacaktır.

#### Doğrulama

```bash
dotnet tool restore
dotnet restore "parca muh.sln"
dotnet build "parca muh.sln" --configuration Release --no-restore -warnaserror
dotnet format "parca muh.sln" --verify-no-changes --no-restore
dotnet tool run dotnet-ef migrations has-pending-model-changes --project AutoPartsStore/Backend/AutoPartsStore.API
dotnet test AutoPartsStore/Backend/AutoPartsStore.API.Tests/AutoPartsStore.API.Tests.csproj --configuration Release --no-build --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
python scripts/check_coverage.py --search-root TestResults --min-line 70 --min-branch 50
dotnet test AutoPartsStore/Backend/AutoPartsStore.API.IntegrationTests/AutoPartsStore.API.IntegrationTests.csproj --configuration Release --no-build
cd AutoPartsStore/Frontend/client
npm run lint
npm test
npm run build
npm audit --audit-level=high
```

Bu kontroller GitHub Actions üzerinde her push ve pull request için çalışır. CI ayrıca SQL Server 2022 üzerinde tüm migration'ları sıfırdan uygulayıp eşzamanlı checkout yarışını, gerçek ASP.NET hostunda auth/RBAC/CORS/header/rate-limit/hata sızıntısı davranışını ve gerçek Chromium oturumunda araç uyumluluğu, mobil taşma, anonim admin yönlendirmesi ve sürümlü yasal kabul içeren checkout/makbuz akışını doğrular. CodeQL `security-extended` ayrı bir iş akışıdır. Yetkili staging hedefi için manuel `Staging assurance` işi; canlı readiness ve entegrasyon sertifikasyonu, k6 eşikleri, ZAP storefront ve OpenAPI taramasını fail-closed çalıştırır.

Staging OpenAPI belgesi varsayılan olarak kapalıdır. Yalnız DAST uygulanan yetkili staging ortamında `OpenApi__Enabled=true` verilmelidir; Swagger UI development dışına açılmaz.

### 📊 Veritabanı Yapısı

#### Ana Tablolar
- **Users**: Kullanıcı bilgileri ve kimlik doğrulama (Email, Password, Role)
- **Products**: Ürün katalogu (Name, Price, Stock, Images)
- **Categories**: Ürün kategorileri (Name, Slug, Description)
- **Brands**: Araç markaları (Name, Slug, LogoUrl)
- **PartBrands**: Parça markaları (Name, Slug, LogoUrl)
- **Orders**: Sipariş bilgileri (OrderNumber, TotalAmount, Status)
- **OrderItems**: Sipariş detayları (Quantity, Price)
- **Payments**: Sağlayıcı, yöntem, tutar ve ödeme yaşam döngüsü
- **PaymentAttempts / PaymentTransactions**: Hosted checkout denemeleri ve sağlayıcı finans hareketleri
- **PaymentEvents**: Ham payload saklamadan hash tabanlı, idempotent sağlayıcı olay kaydı
- **Refunds**: Eşzamanlı fazla iadeyi engelleyen tam/kısmi iade durumu
- **OutboxMessages**: Lease, bounded batch ve üst sınırlı retry ile dayanıklı entegrasyon kuyruğu
- **Shipments / ShipmentItems**: Parçalı sevkiyat, benzersiz kargo/takip ve satır bazlı adet
- **ReturnRequests / ReturnItems**: RMA durum makinesi, neden kodu, miktar kapasitesi ve doğrulanmış refund referansı
- **VehicleMakes / VehicleModels / VehicleGenerations / VehicleEngines / Vehicles**: Provider bağımsız araç ağacı
- **ProductFitments / ProductIdentifiers**: Kaynak kanıtlı ürün–araç uyumu ve normalize OEM/interchange kodları
- **InventoryReservations / InventoryReservationItems**: Süreli, idempotent ve yarış güvenli stok ayırma temeli
- **AdminAuditEvents**: Metadata-only, zincir bütünlüklü admin olayları
- **DealerApplications / CustomerGroups / PriceLists / PriceRules / BulkQuoteRequests**: B2B müşteri ve fiyat/teklif akışları
- **Suppliers / SupplierOffers**: Çok tedarikçili teklif ve deterministik kaynak seçimi
- **SalesChannels / ChannelListings / ChannelOrderLinks / ChannelInboxEvents**: Pazaryeri yetenek, listing ve idempotent sipariş temeli
- **UserVehicles / MaintenanceRecords / MaintenanceRecordItems / MaintenanceReminders**: Garaj, kilometre, servis günlüğü ve hatırlatıcılar; plaka/VIN tutulmaz
- **Reviews**: Ürün değerlendirmeleri (Rating 1-5, Comment)

**Tüm modeller Data Annotations ile validate edilir!**

### 🔌 API Endpoints

#### Auth
- `POST /api/Auth/register` - Kullanıcı kaydı
- `POST /api/Auth/login` - Kullanıcı girişi
- `GET /api/Auth/me` - Kullanıcı bilgisi
- `PUT /api/Auth/update-profile` - Profil güncelleme

#### Products
- `GET /api/Products` - Tüm ürünleri listele
- `GET /api/Products/{id}` - Ürün detayı
- `GET /api/Products/search?query=` - Ürün arama
- `GET /api/Products/category/{slug}` - Kategoriye göre ürünler
- `GET /api/Products/brand/{slug}` - Markaya göre ürünler

#### Orders
- `GET /api/Orders` - Kullanıcının siparişleri
- `POST /api/Orders` - `Idempotency-Key` başlığıyla atomik sipariş oluştur
- `GET /api/Orders/{id}` - Sipariş detayı
- `POST /api/Orders/track` - Sipariş numarası ve e-posta ile PII içermeyen misafir takibi

#### Payments
- `GET /api/Payments/capabilities` - Aktif ödeme yöntemlerini bildirir; gerçek gateway yapılandırılana kadar online kart `false`

#### Fitment
- `GET /api/Fitment/vehicles/{makes|models|generations|engines|configurations}` - Kademeli araç seçimi
- `GET /api/Fitment/check?productId=&vehicleId=` - Doğrulanmış ürün–araç uyumluluk sonucu
- `GET /api/Fitment/products/{productId}` - Ürünün doğrulanmış araç kayıtları

#### Garage and maintenance
- `GET|POST /api/Garage` - Kullanıcının araçları / idempotent araç ekleme
- `PUT /api/Garage/{id}` - Optimistic concurrency ile araç/km güncelleme
- `GET|POST /api/Garage/{id}/maintenance` - Bakım geçmişi ve idempotent kayıt
- `GET|POST /api/Garage/{id}/reminders` - Tarih/km hedefli hesap içi hatırlatıcı
- `POST /api/Garage/reminders/{id}/complete` - Hatırlatıcıyı tamamla

#### SEO
- `GET /sitemap.xml` - Sitemap index
- `GET /sitemaps/static.xml` ve `/sitemaps/products-{page}.xml` - Bölünmüş sitemap’ler
- `GET /robots.txt` - Public site origin’i yoksa güvenli `Disallow: /`
- `GET /api/Fitment/identifiers/{value}` - Normalize edilmiş ve doğrulanmış parça kodu araması

#### Reviews
- `GET /api/Reviews/product/{productId}` - Ürün incelemeleri
- `POST /api/Reviews` - Yorum ekle
- `PUT /api/Reviews/{id}` - Yorum güncelle
- `DELETE /api/Reviews/{id}` - Yorum sil

#### Admin (Policy-gated)
- `GET /api/Admin/stats` - Dashboard istatistikleri
- `GET /api/Admin/products` - Tüm ürünler (admin)
- `POST /api/Admin/products` - Ürün ekle
- `PUT /api/Admin/products/{id}` - Ürün güncelle
- `DELETE /api/Admin/products/{id}` - Ürün sil
- `GET /api/Admin/orders` - Tüm siparişler
- `PUT /api/Admin/orders/{id}/status` - Geçerli durum geçişleriyle siparişi güncelle
- `GET /api/Admin/payments` - Ödeme kayıtlarını listele
- `POST /api/Admin/payments/{id}/mark-paid` - Teslimatta ödemeyi tahsil edildi işaretle
- `GET /api/Admin/shipments` - Sevkiyatları ve kalemlerini listele
- `POST /api/Admin/orders/{id}/shipments` - `Idempotency-Key` ile kısmi/çoklu sevkiyat oluştur
- `POST /api/Admin/shipments/{id}/{command}` - Etiket, hazır, sevk, teslim, hata ve iptal komutları
- `GET /api/Admin/returns` - RMA/iade taleplerini listele
- `POST /api/Admin/orders/{id}/returns` - `Idempotency-Key` ve neden kodlarıyla iade talebi oluştur
- `POST /api/Admin/returns/{id}/{command}` - Onay, ret, teslim alma, inceleme, iptal ve kapama komutları
- `GET /api/Admin/integrations/capabilities` - Secret göstermeden ödeme ve e-belge hazır olma durumunu bildir
- `POST /api/Admin/fitment/vehicles` - Araç ağacı upsert
- `POST /api/Admin/fitment/links` - Kaynak kanıtlı ürün–araç uyumu upsert
- `POST /api/Admin/fitment/identifiers` - OEM/interchange kodu upsert
- `GET /api/Admin/audit` - Bounded audit metadata sayfası
- `GET /api/Admin/audit/verify` - Audit zincir bütünlüğünü doğrula

Admin API’si `RefundPending` veya `Refunded` durumlarını genel bir komutla vermez. Bu durumlar yalnız gerçek ödeme iadesinin doğrulanmış harici referanslarıyla ilerletilmek üzere servis sınırında tutulur.

### 👤 İlk Yönetici

Repo varsayılan yönetici veya test parolası oluşturmaz. İlk yönetici production ortamında kontrollü bir bootstrap/secret süreciyle oluşturulmalıdır.

### 📧 Email Yapılandırması

Email bildirimleri için `appsettings.json` dosyasında SMTP ayarlarını yapılandırın:

```json
{
  "EmailSettings": {
    "SmtpServer": "smtp.gmail.com",
    "SmtpPort": 587,
    "SenderEmail": "your-email@gmail.com",
    "SenderName": "AutoParts Store",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password"
  }
}
```

**Not**: Gmail kullanıyorsanız, 2FA etkinleştirip [App Password](https://support.google.com/accounts/answer/185833) oluşturmanız gerekir.

### 📁 Proje Yapısı

```
AutoPartsStore/
├── Backend/
│   └── AutoPartsStore.API/
│       ├── Controllers/          # API Controllers (Auth, Products, Orders, Admin)
│       ├── Data/                 # DbContext ve Seed Data
│       ├── Migrations/           # Entity Framework Migrations
│       ├── Models/               # Entity Models (with Data Annotations validations)
│       ├── Services/             # Business Logic (JwtService, EmailService)
│       ├── Properties/           # Launch settings
│       ├── appsettings.json      # Production config (no secrets!)
│       ├── appsettings.Development.json  # Development config (JWT Key)
│       ├── Program.cs            # Application entry point & middleware
│       └── AutoPartsStore.API.csproj
└── Frontend/
    └── client/
        ├── src/
        │   ├── components/       # Reusable components
        │   │   ├── Header.jsx
        │   │   ├── Footer.jsx
        │   │   ├── ErrorBoundary.jsx
        │   │   ├── NotificationContainer.jsx
        │   │   ├── ProductCard.jsx
        │   │   ├── Pagination.jsx
        │   │   └── VehicleSearchBar.jsx
        │   ├── context/          # React Context (Global State)
        │   │   ├── AuthContext.jsx
        │   │   ├── CartContext.jsx
        │   │   ├── WishlistContext.jsx
        │   │   └── NotificationContext.jsx
        │   ├── pages/            # Page Components (Home, Products, Cart, etc.)
        │   ├── services/         # API Services
        │   │   └── api.js        # Axios instance with interceptors
        │   ├── assets/           # Static assets (images, icons)
        │   ├── App.jsx           # Main app component
        │   ├── App.css           # Global styles
        │   ├── main.jsx          # React entry point
        │   ├── index.css         # Base CSS
        │   └── auth-admin-styles.css  # Admin panel styles
        ├── .env                  # Environment variables (VITE_API_BASE_URL)
        ├── package.json
        ├── vite.config.js
        └── index.html
```

### 🎨 Öne Çıkan Sayfalar

#### Müşteri Arayüzü
- **Ana Sayfa**: Öne çıkan ürünler ve kategoriler
- **Kategori Sayfası**: Kategoriye göre filtrelenmiş ürünler
- **Ürün Detay**: Ürün bilgileri, yorumlar ve sepete ekleme
- **Sepet**: Sepet yönetimi ve ödeme
- **Profil**: Kullanıcı bilgileri ve istatistikler
- **Sipariş Geçmişi**: Geçmiş siparişler ve detayları
- **Favoriler**: Favori ürün listesi
- **404 Sayfa**: Özel bulunamadı sayfası

#### Admin Paneli
- **Dashboard**: Genel istatistikler (sade beyaz kartlar)
- **Ürün Yönetimi**: Ürün CRUD işlemleri
- **Sipariş Yönetimi**: Sipariş durumu güncelleme

### 💻 Geliştirme Notları

#### Veritabanı Migration
```bash
# Yeni migration oluştur
dotnet ef migrations add MigrationName

# Veritabanını güncelle
dotnet ef database update

# Veritabanını sıfırla
dotnet ef database drop -f
```

#### Frontend Build
```bash
# Production build
npm run build

# Preview production build
npm run preview
```

### 🔒 Güvenlik

- ✅ Şifreler BCrypt ile hash'lenir
- ✅ JWT token'lar ile güvenli kimlik doğrulama
- ✅ Role-based authorization (Admin/User)
- ✅ CORS yapılandırması (geliştirme: localhost)
- ✅ Input validasyonu (tüm modellerde Data Annotations)
- ✅ SQL injection koruması (EF Core parametreli sorgular)
- ✅ Global Exception Handler
- ✅ Environment variables ile güvenli konfigürasyon
- ✅ AllowedHosts kısıtlaması
- ⚠️ Genel frontend console temizliği yapıldı; provider exception ve PII redaction politikası tamamlanmadan backend logları production-safe sayılmaz

### ⚡ Performans Optimizasyonları

- Lazy loading
- Sayfalama (pagination) - 12 ürün/sayfa
- Index kullanımı
- Response caching
- Optimized database queries
- React Context API ile efficient state management

### 🤝 Katkıda Bulunma

1. Fork edin
2. Feature branch oluşturun (`git checkout -b feature/AmazingFeature`)
3. Değişikliklerinizi commit edin (`git commit -m 'Add some AmazingFeature'`)
4. Branch'inizi push edin (`git push origin feature/AmazingFeature`)
5. Pull Request oluşturun

### 📝 Lisans

Bu proje eğitim amaçlı geliştirilmiştir.

### 📞 İletişim

Proje Sahibi - Mustafa Ataklı

Proje Link: [https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem](https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem)

### 📸 Ekran Görüntüleri

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151007.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140640.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140742.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140859.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140918.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20141126.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20141252.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20141210.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004204.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004237.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004258.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151438.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151223.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004416.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004632.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151154.png" width="auto">

---

### 🚀 Gelecek Özellikler

- [ ] Akıllı Ürün Öneri Sistemi (AI Recommender System)
- [ ] Yorum Analizi (Sentiment Analysis)
- [ ] Akıllı Chatbot (AI Customer Assistant)
- [ ] Canlı chat desteği
- [ ] Sahte Yorum Tespiti
- [ ] Ürün karşılaştırma
- [ ] Gelişmiş filtreleme seçenekleri
- [ ] UBL-TR elektronik asıl, doğrulama/görüntüleme bileşeni ve izin verilen müşteri görünümüyle e-Fatura/e-Arşiv arşivi
- [ ] Çoklu dil desteği
- [ ] Kampanya ve kupon sistemi
- [ ] SMS bildirimleri
- [ ] Sosyal medya entegrasyonu
- [ ] HttpOnly cookie için token storage
- [ ] Password complexity artırma
- [ ] Checkout, provider webhook ve kullanıcı yazma uçlarında dağıtık rate-limit store’u (yerel fixed-window politikaları tamamlandı)

### 🙏 Teşekkürler

Bu projeyi geliştirirken kullanılan açık kaynak kütüphanelere ve topluluk katkılarına teşekkürler.

### ⭐ Yıldız Verin!

Bu projeyi beğendiyseniz yıldız vermeyi unutmayın! ⭐
---

<a name="english"></a>
## 🇬🇧 English

A development-stage automotive spare parts e-commerce platform built with an ASP.NET Core 9.0 Web API backend and React 19 frontend.

> **Status**: The project is not production-ready yet. Server-priced checkout, hosted-payment reservation coordination, payment/refund state machines, partial shipment/RMA, B2B pricing/RFQ/supplier foundations, fail-closed marketplace boundaries, verified fitment and garage/maintenance flows are implemented. Real payment, carrier, electronic-invoice and marketplace adapters intentionally remain disabled until credentials, legal/KVKK decisions, sandbox certification and real SQL Server gates are complete.

### 🎯 Project Highlights

- **Secure Development Baseline**: Mandatory external JWT secret, protected order lookup and rate-limited guest tracking
- **Comprehensive Validation**: Full validation with Data Annotations on 8 entity models
- **Controlled Commerce Core**: Idempotent checkout, monotonic payment/refund transitions and append-only provider event records
- **Secure Configuration**: Environment variables, JWT secret management
- **Error Handling**: Global Exception Handler + Error Boundary
- **API Documentation**: Consistent endpoint structure with RESTful API

### ✨ Features

#### Customer Features
- **Product Catalog System**: Filter by categories and brands
- **Advanced Search**: Search by product name, brand, and part number
- **Cart Management**: Real-time cart updates
- **Wishlist**: Save and manage favorite products (persistent with localStorage)
- **User Profile**: Profile information and order statistics
- **Order History**: Detailed view of all orders
- **Product Reviews**: Star-based rating and comment system
- **Garage and Maintenance Journal**: Multiple catalog vehicles, odometer, service history, date/km reminders and links back to previously used parts
- **Verified Fitment Confidence**: Thresholded confidence bands and automatic product checks for the selected garage vehicle
- **B2B Center**: Dealer application, bulk RFQ, quote review and acceptance
- **Notification System**: User feedback with toast notifications
- **Pagination**: Efficient product listing (12 products/page)
- **404 Page**: Custom error page
- **Error Boundary**: React error catching mechanism

#### Admin Features
- **Dashboard**: Professional statistics and overview
- **Product Management**: CRUD operations (Create, Update, Delete)
- **Order Management**: Order status updates
- **Category Management**: Create and edit categories
- **Brand Management**: Manage vehicle and part brands
- **Stock Control**: Automatic inventory tracking
- **Email Notifications**: Automatic emails for order confirmations
- **Payment Operations**: Payment list, manual collection and gross/refund/net finance summary
- **Fulfillment Operations**: Partial/multiple shipments, tracking data and explicit shipment commands
- **RMA Operations**: Quantity-bounded return requests and inspection lifecycle for delivered orders
- **Integration Readiness**: Fail-closed payment and Turkish electronic-invoice capability status
- **B2B, Supplier and Channel Operations**: Price groups/rules, RFQ, supplier sourcing and marketplace drift/inbox status
- **Garage Support View**: Read-only operational metadata without exposing email, phone, VIN, plate or private maintenance notes

#### Technical Features
- **JWT Authentication**: Secure authentication
- **Role-Based Authorization**: Admin and user roles
- **Responsive Design**: Mobile-friendly interface
- **Context API**: Global state management
- **RESTful API**: Standard API structure
- **Entity Framework Core**: ORM and database management
- **Global Exception Handler**: Centralized error handling
- **Model Validations**: Data Annotations on all models
- **Environment Variables**: Secure configuration management
- **Hosted-payment boundary**: No card/PAN/CVV model; an unconfigured online gateway fails closed
- **Durable processing foundation**: PaymentAttempt, PaymentTransaction, Refund, PaymentEvent and bounded outbox
- **After-sales core**: Shipment/ShipmentItem and ReturnRequest/ReturnItem with idempotency, concurrency and quantity bounds
- **Turkish e-document boundary**: Provider-neutral immutable amount/tax snapshots and a disabled gateway that never fabricates success
- **Garage persistence**: Ownership-scoped, idempotent and concurrency-safe maintenance/reminder records without plate or VIN storage
- **SEO and web security**: Product JSON-LD/canonical metadata, paged sitemaps, fail-closed robots, validated exact-origin CORS, HSTS and production security headers

### 🏗️ Technology and Architecture Decisions

#### Backend Architecture
- **Entity Framework Core**: Database management with Code-First approach
- **Controller + service boundaries**: Critical checkout, order, payment, refund and outbox behavior lives in services
- **DTO Pattern**: Controlled admin and external API response models
- **Dependency Injection**: .NET Core built-in DI container
- **Middleware Pipeline**: Global error catching and authentication

#### Frontend Architecture
- **Context API**: Global state management (Auth, Cart, Wishlist, Notification)
- **Component-Based**: Reusable and modular component structure
- **Custom Hooks**: useAuth, useCart, useWishlist, useNotification
- **Axios Interceptors**: Automatic token injection and error handling
- **Error Boundary**: React error catching pattern

#### Security Strategy
- **BCrypt**: Password hashing (cost factor: 10)
- **JWT**: Stateless authentication (24-hour expiration)
- **Data Annotations**: Model-level input validation
- **Environment Variables**: Secure storage of sensitive information
- **CORS Policy**: Origin control
- **Rate limiting**: IP-based limits for authentication and guest order tracking
- **Secret/PII protection**: No default admin password; provider tokens, identity and idempotency fields are excluded from general JSON

### 🛠 Technology Stack

#### Backend
- ASP.NET Core 9.0 Web API
- Entity Framework Core 9.0
- SQL Server / SQL Server LocalDB
- JWT Authentication
- AutoMapper
- BCrypt.Net (Password hashing)
- MailKit (Email service)

#### Frontend
- React 19
- React Router v8.3
- Axios
- Context API (Auth, Cart, Wishlist, Notification)
- CSS3 (Custom styling)

### 📦 Installation

#### Requirements
- .NET 9.0 SDK
- Node.js (v20 or higher; CI uses Node.js 22)
- SQL Server or SQL Server LocalDB
- Git

#### Backend Setup

1. Clone the repository:
```bash
git clone <repository-url>
cd AutoPartsStore/Backend/AutoPartsStore.API
```

2. Configure the connection string:
Update the SQL Server connection string in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=AutoPartsDb;Trusted_Connection=true;TrustServerCertificate=true"
  }
}
```

3. **IMPORTANT**: Set the JWT secret key:
The key is not stored in the repository. Use an environment variable with at least 32 characters in development and production:
```bash
export Jwt__Key="replace-with-a-random-secret-of-at-least-32-characters"
```

4. Create the database:
```bash
dotnet ef database update
```

5. Run the application:
```bash
dotnet run
```

The Backend API will run at `http://localhost:5167`.

#### Frontend Setup

1. Navigate to the frontend folder:
```bash
cd ../../Frontend/client
```

2. Create environment file:
`.env` file exists. Content:
```env
VITE_API_BASE_URL=http://localhost:5167/api
```

3. Install the locked dependencies:
```bash
npm ci
```

4. Start the development server:
```bash
npm run dev
```

The frontend application will run at `http://localhost:5173`.

### 📊 Database Structure

#### Main Tables
- **Users**: User information and authentication (Email, Password, Role)
- **Products**: Product catalog (Name, Price, Stock, Images)
- **Categories**: Product categories (Name, Slug, Description)
- **Brands**: Vehicle brands (Name, Slug, LogoUrl)
- **PartBrands**: Part brands (Name, Slug, LogoUrl)
- **Orders**: Order information (OrderNumber, TotalAmount, Status)
- **OrderItems**: Order details (Quantity, Price)
- **Payments**: Provider, method, amount and payment lifecycle
- **PaymentAttempts / PaymentTransactions**: Hosted checkout attempts and provider financial movements
- **PaymentEvents**: Hash-based idempotent provider events without raw payload storage
- **Refunds**: Full/partial refund state with concurrent over-refund protection
- **OutboxMessages**: Durable integration queue with leases, bounded batches and capped retries
- **Reviews**: Product reviews (Rating 1-5, Comment)

**All models are validated with Data Annotations!**

### 🔌 API Endpoints

#### Auth
- `POST /api/Auth/register` - User registration
- `POST /api/Auth/login` - User login
- `GET /api/Auth/me` - Get user info
- `PUT /api/Auth/update-profile` - Update profile

#### Products
- `GET /api/Products` - List all products
- `GET /api/Products/{id}` - Product details
- `GET /api/Products/search?query=` - Search products
- `GET /api/Products/category/{slug}` - Products by category
- `GET /api/Products/brand/{slug}` - Products by brand

#### Orders
- `GET /api/Orders` - User's orders
- `POST /api/Orders` - Create an atomic order with an `Idempotency-Key` header
- `GET /api/Orders/{id}` - Order details
- `POST /api/Orders/track` - PII-minimized guest tracking with order number and email

#### Payments
- `GET /api/Payments/capabilities` - Reports enabled payment methods; online card remains `false` until a real gateway is configured

#### Reviews
- `GET /api/Reviews/product/{productId}` - Product reviews
- `POST /api/Reviews` - Add review
- `PUT /api/Reviews/{id}` - Update review
- `DELETE /api/Reviews/{id}` - Delete review

#### Admin (Policy-gated)
- `GET /api/Admin/stats` - Dashboard statistics
- `GET /api/Admin/products` - All products (admin)
- `POST /api/Admin/products` - Add product
- `PUT /api/Admin/products/{id}` - Update product
- `DELETE /api/Admin/products/{id}` - Delete product
- `GET /api/Admin/orders` - All orders
- `PUT /api/Admin/orders/{id}/status` - Update an order through valid transitions
- `GET /api/Admin/payments` - List payment records
- `POST /api/Admin/payments/{id}/mark-paid` - Mark pay-at-delivery collection as paid
- `GET /api/Admin/shipments` - List shipments and their lines
- `POST /api/Admin/orders/{id}/shipments` - Create a partial/multiple shipment with `Idempotency-Key`
- `POST /api/Admin/shipments/{id}/{command}` - Explicit label, ready, ship, deliver, fail and cancel commands
- `GET /api/Admin/returns` - List RMA/return requests
- `POST /api/Admin/orders/{id}/returns` - Create a return request with `Idempotency-Key` and reason codes
- `POST /api/Admin/returns/{id}/{command}` - Explicit approve, reject, receive, inspect, cancel and close commands
- `GET /api/Admin/integrations/capabilities` - Report payment and e-document readiness without exposing secrets

The admin API does not provide a generic command for `RefundPending` or `Refunded`. Those states remain behind the service boundary for verified external refund references only.

### 👤 Initial Administrator

The repository does not create a default administrator or test password. Provision the first administrator through a controlled production bootstrap/secret process.

### 📧 Email Configuration

Configure SMTP settings in `appsettings.json` for email notifications:

```json
{
  "EmailSettings": {
    "SmtpServer": "smtp.gmail.com",
    "SmtpPort": 587,
    "SenderEmail": "your-email@gmail.com",
    "SenderName": "AutoParts Store",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password"
  }
}
```

**Note**: If using Gmail, you need to enable 2FA and create an [App Password](https://support.google.com/accounts/answer/185833).

### 📁 Project Structure

```
AutoPartsStore/
├── Backend/
│   └── AutoPartsStore.API/
│       ├── Controllers/          # API Controllers (Auth, Products, Orders, Admin)
│       ├── Data/                 # DbContext and Seed Data
│       ├── Migrations/           # Entity Framework Migrations
│       ├── Models/               # Entity Models (with Data Annotations validations)
│       ├── Services/             # Business Logic (JwtService, EmailService)
│       ├── Properties/           # Launch settings
│       ├── appsettings.json      # Production config (no secrets!)
│       ├── appsettings.Development.json  # Development config (JWT Key)
│       ├── Program.cs            # Application entry point & middleware
│       └── AutoPartsStore.API.csproj
└── Frontend/
    └── client/
        ├── src/
        │   ├── components/       # Reusable components
        │   │   ├── Header.jsx
        │   │   ├── Footer.jsx
        │   │   ├── ErrorBoundary.jsx
        │   │   ├── NotificationContainer.jsx
        │   │   ├── ProductCard.jsx
        │   │   ├── Pagination.jsx
        │   │   └── VehicleSearchBar.jsx
        │   ├── context/          # React Context (Global State)
        │   │   ├── AuthContext.jsx
        │   │   ├── CartContext.jsx
        │   │   ├── WishlistContext.jsx
        │   │   └── NotificationContext.jsx
        │   ├── pages/            # Page Components (Home, Products, Cart, etc.)
        │   ├── services/         # API Services
        │   │   └── api.js        # Axios instance with interceptors
        │   ├── assets/           # Static assets (images, icons)
        │   ├── App.jsx           # Main app component
        │   ├── App.css           # Global styles
        │   ├── main.jsx          # React entry point
        │   ├── index.css         # Base CSS
        │   └── auth-admin-styles.css  # Admin panel styles
        ├── .env                  # Environment variables (VITE_API_BASE_URL)
        ├── package.json
        ├── vite.config.js
        └── index.html
```

### 🎨 Key Pages

#### Customer Interface
- **Home Page**: Featured products and categories
- **Category Page**: Filtered products by category
- **Product Detail**: Product information, reviews, and add to cart
- **Cart**: Cart management and checkout
- **Profile**: User information and statistics
- **Order History**: Past orders and details
- **Wishlist**: Favorite product list
- **404 Page**: Custom not found page

#### Admin Panel
- **Dashboard**: General statistics (clean white cards)
- **Product Management**: Product CRUD operations
- **Order Management**: Update order status

### 💻 Development Notes

#### Database Migration
```bash
# Create new migration
dotnet ef migrations add MigrationName

# Update database
dotnet ef database update

# Reset database
dotnet ef database drop -f
```

#### Frontend Build
```bash
# Production build
npm run build

# Preview production build
npm run preview
```

### 🔒 Security

- ✅ Passwords are hashed with BCrypt
- ✅ Secure authentication with JWT tokens
- ✅ Role-based authorization (Admin/User)
- ✅ CORS configuration (development: localhost)
- ✅ Input validation (Data Annotations on all models)
- ✅ SQL injection protection (EF Core parameterized queries)
- ✅ Global Exception Handler
- ✅ Secure configuration with environment variables
- ✅ AllowedHosts restriction
- ⚠️ General frontend console cleanup is complete; backend logs are not considered production-safe until provider exception and PII redaction policy is enforced

### ⚡ Performance Optimizations

- Lazy loading
- Pagination - 12 products/page
- Index usage
- Response caching
- Optimized database queries
- Efficient state management with React Context API

### 🤝 Contributing

1. Fork the project
2. Create a feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Create a Pull Request

### 📝 License

This project was developed for educational purposes.

### 📞 Contact

Project Owner - Mustafa Ataklı

Project Link: [https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem](https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem)

### 📸 Screenshots

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151007.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140640.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140742.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140859.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20140918.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20141126.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20141252.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-03%20141210.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004204.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004237.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004258.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151438.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151223.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004416.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20004632.png" width="auto">

---

<img src="https://github.com/mustafaatakli/ParcaMuhendisi-AutoPartsSystem/blob/main/photos/Ekran%20g%C3%B6r%C3%BCnt%C3%BCs%C3%BC%202025-11-04%20151154.png" width="auto">

---

### 🚀 Future Features

- [ ] AI Recommender System
- [ ] Sentiment analysis for comments
- [ ] AI Customer Assistant
- [ ] Live chat support
- [ ] Fake Review Detection
- [ ] Product comparison
- [ ] Advanced filtering options
- [ ] e-Invoice/e-Archive storage for the signed UBL-TR original plus validation/rendering components and an allowed customer view
- [ ] Multi-language support
- [ ] Campaign and coupon system
- [ ] SMS notifications
- [ ] Social media integration
- [ ] HttpOnly cookie for token storage
- [ ] Enhanced password complexity
- [ ] Distributed rate-limit storage for checkout, provider webhooks and user writes (local fixed-window policies are implemented)

### 🙏 Acknowledgments

Thanks to the open-source libraries and community contributions used in developing this project.

### ⭐ Give it a Star!

If you liked this project, don't forget to give it a star! ⭐
