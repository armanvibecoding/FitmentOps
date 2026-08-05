# FitmentOps

**Araç uyumluluğu odaklı otomotiv ticaret ve operasyon platformu**

[Ana README](../README.md) · [Güvenlik politikası](../SECURITY.md) · [Katkı rehberi](../CONTRIBUTING.md)

FitmentOps; araç uyumluluğu kanıtını, ürün keşfini, checkout işlemlerini, ödeme ve iade durumlarını, sevkiyatı, RMA süreçlerini, B2B fiyatlandırmayı, tedarikçi yönetimini ve yönetim operasyonlarını tek platformda birleştirir.

> [!IMPORTANT]
> Proje üretim öncesi mühendislik aşamasındadır. Gerçek ödeme, e-belge, kargo ve pazaryeri adaptörleri; sağlayıcı sözleşmeleri, hukuki kontroller ve sandbox sertifikasyonu tamamlanana kadar güvenli biçimde kapalıdır.

## Temel fark

Standart e-ticarette ürünün stokta olması yeterli olabilir. Otomotiv satış sonrası pazarında ise doğru ürünün yanlış araca satılması ciddi iade ve güvenlik riski oluşturur. FitmentOps bu nedenle araç uyumluluğunu basit bir filtre değil, kaynak ve güven seviyesiyle kanıtlanan bir domain kararı olarak ele alır.

- Kanıt yoksa sistem olumlu uyumluluk iddiasında bulunmaz ve `Unknown` döner.
- Fiyat ve stok tarayıcıdan kabul edilmez; sunucuda yeniden hesaplanır.
- Checkout işlemleri idempotency anahtarı ve süreli stok rezervasyonuyla korunur.
- Ödeme, iade, sevkiyat ve RMA geçişleri açık durum makineleriyle yönetilir.
- Yapılandırılmamış sağlayıcı başarılıymış gibi davranmaz.
- Kritik yönetim işlemleri rol/politika ayrımı ve doğrulanabilir audit zinciriyle korunur.

## Özellik alanları

### Müşteri deneyimi

- Kategori, marka, ürün kodu ve OEM/interchange kodu araması
- Marka → model → nesil → motor → konfigürasyon araç ağacı
- `Exact`, `Compatible` ve güvenli `Unknown` uyumluluk sonucu
- Çoklu araç garajı, kilometre ve bakım günlüğü
- Favoriler, sepet, profil, sipariş geçmişi ve takip
- Sürümlü ön bilgilendirme ve mesafeli satış kabulü
- Bayi başvurusu, toplu RFQ ve teklif kabulü

### Ticaret çekirdeği

- Sunucu fiyatlı ve idempotent sipariş oluşturma
- Süreli ve eşzamanlılığa dayanıklı stok rezervasyonu
- Hosted-payment sağlayıcı sınırı
- Ödeme girişimi, işlem, provider olayı ve mutabakat kayıtları
- Tam/kısmi iade ve fazla iade engeli
- Çoklu/kısmi sevkiyat ve takip bilgisi
- Adet kontrollü iade/RMA ve inceleme yaşam döngüsü
- Transactional outbox ve bounded worker yapısı

### Yönetim ve operasyon

- Ürün, kategori, marka, sipariş ve kullanıcı yönetimi
- Ödeme, iade, sevkiyat ve RMA operasyon ekranları
- Araç ağacı, uyumluluk, kod, kaynak ve güven yönetimi
- Sürümlü yasal metin yayınlama
- B2B fiyat listesi, kural, RFQ ve tedarikçi teklifi yönetimi
- Pazaryeri yetenek, fiyat/stok sapması ve inbox görünümü
- SHA-256 zincirli append-only audit kontrolü
- Health, readiness, correlation ID ve operasyon backlog ölçümleri

## Hızlı başlangıç

Gereksinimler:

- .NET SDK 9
- Node.js 22+
- SQL Server veya SQL Server LocalDB
- Git

```bash
git clone https://github.com/armanvibecoding/FitmentOps.git
cd FitmentOps
dotnet tool restore
dotnet restore FitmentOps.sln
```

JWT anahtarını ve gerekirse bağlantı dizesini ortam değişkeniyle tanımlayın:

```powershell
$env:Jwt__Key = "en-az-32-karakterlik-rastgele-gelistirme-anahtari"
$env:ConnectionStrings__DefaultConnection = "Server=(localdb)\mssqllocaldb;Database=FitmentOpsDb;Trusted_Connection=true;TrustServerCertificate=true"
```

Veritabanını oluşturup API’yi çalıştırın:

```bash
dotnet tool run dotnet-ef database update --project AutoPartsStore/Backend/AutoPartsStore.API/AutoPartsStore.API.csproj --startup-project AutoPartsStore/Backend/AutoPartsStore.API/AutoPartsStore.API.csproj
dotnet run --project AutoPartsStore/Backend/AutoPartsStore.API/AutoPartsStore.API.csproj
```

Frontend için `AutoPartsStore/Frontend/client/.env.example` dosyasını `.env.local` olarak kopyalayıp değerleri doldurun. Ardından:

```bash
cd AutoPartsStore/Frontend/client
npm ci
npm run dev
```

## Entegrasyonların gerçek durumu

| Alan | Varsayılan durum | Canlıya geçiş koşulu |
| --- | --- | --- |
| Online ödeme | Kapalı ve fail-closed | Gerçek gateway adaptörü, credential güvenliği, callback doğrulaması ve sandbox sertifikasyonu |
| E-belge | Kapalı ve fail-closed | Sağlayıcı adaptörü, UBL-TR/provider çıktısı doğrulaması ve hukuki onay |
| Kargo | Domain akışı hazır | Taşıyıcı adaptörü, etiket/takip mutabakatı ve hata kurtarma |
| Pazaryerleri | Sınırlar hazır | Kanal bazlı listing, sipariş, webhook, rate-limit ve drift adaptörleri |
| SMTP | İsteğe bağlı | TLS destekli SMTP yapılandırması |

iyzico için imzalama ve yanıt imzası doğrulama primitive’leri bulunur; ancak canlı bir iyzico gateway’i kayıtlı değildir. Bu iki durum birbirine eşit değildir.

## Test ve kalite

Ana doğrulama komutları:

```bash
dotnet build FitmentOps.sln --configuration Release --no-restore -warnaserror
dotnet format FitmentOps.sln --verify-no-changes --no-restore
dotnet test AutoPartsStore/Backend/AutoPartsStore.API.Tests/AutoPartsStore.API.Tests.csproj --configuration Release --no-build
dotnet test AutoPartsStore/Backend/AutoPartsStore.API.IntegrationTests/AutoPartsStore.API.IntegrationTests.csproj --configuration Release --no-build
python scripts/scan_secrets.py

cd AutoPartsStore/Frontend/client
npm run lint
npm test
npm run build
npm audit --audit-level=high
```

CI; gerçek SQL Server migration/eşzamanlı checkout testlerini de çalıştırır. CodeQL ayrı workflow’dur. Staging assurance ise gerçek staging URL ve kimlik bilgileri olmadan başarılı sayılmaz.

## Canlıya geçiş kontrolü

Canlı satıştan önce en az şu işler tamamlanmalıdır:

1. Üretim SQL Server yedekleme, geri dönüş ve migration runbook’u.
2. Ödeme ve e-belge sağlayıcı sandbox sertifikasyonu.
3. Kargo ve pazaryeri hata/mutabakat senaryoları.
4. KVKK, çerez, mesafeli satış, iade ve veri saklama kararları.
5. Secret store, TLS, DNS, e-posta doğrulaması ve erişim politikaları.
6. Staging Playwright, ZAP ve k6 kapılarının gerçek ortamda geçmesi.
7. Alarm, dashboard, on-call ve olay müdahale süreçleri.

## Lisans

Proje [Apache License 2.0](../LICENSE) ile lisanslanmıştır. Ürün adları ve harici sağlayıcı markaları ilgili sahiplerine aittir.
