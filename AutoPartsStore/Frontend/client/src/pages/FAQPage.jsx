import { useState } from 'react';
import { Link } from 'react-router';
import { brand } from '../config/brand';
import './InfoPages.css';

const faqCategories = {
  genel: {
    title: 'Platform',
    questions: [
      {
        q: `${brand.name} nedir?`,
        a: 'Araç uyumluluğu odaklı ürün keşfi ile sipariş, ödeme, sevkiyat, iade, B2B ve yönetim operasyonlarını birleştiren otomotiv ticaret platformudur.',
      },
      {
        q: 'Platform canlı satışa hazır mı?',
        a: 'Çekirdek akışlar ve otomasyon testleri hazırdır; gerçek satış öncesinde ödeme, e-belge, kargo ve pazaryeri sağlayıcılarının sandbox sertifikasyonu ile hukuki kontroller tamamlanmalıdır.',
      },
    ],
  },
  uyumluluk: {
    title: 'Araç Uyumluluğu',
    questions: [
      {
        q: 'Bir parçanın aracıma uyduğu nasıl belirleniyor?',
        a: 'Marka, model, nesil, motor ve konfigürasyon seçimi doğrulanmış katalog kayıtlarıyla karşılaştırılır. Sonuç Exact, Compatible veya Unknown olarak gösterilir.',
      },
      {
        q: 'Unknown sonucu ne anlama gelir?',
        a: 'Sistemde olumlu uyumluluk iddiası kurmak için yeterli kanıt bulunmadığı anlamına gelir. OEM kodu ve araç bilgileri doğrulanmadan satın alma kararı verilmemelidir.',
      },
      {
        q: 'OEM veya üretici koduyla arama yapılabilir mi?',
        a: 'Evet. Normalize edilmiş OEM, üretici ve interchange kodları katalog kaynağı ve geçerlilik bilgisiyle yönetilir.',
      },
    ],
  },
  siparis: {
    title: 'Sipariş ve Ödeme',
    questions: [
      {
        q: 'Checkout fiyatı nerede hesaplanır?',
        a: 'Ürün, stok ve toplam tutar istemciden kabul edilmez; güncel değerler sunucuda yeniden hesaplanır ve idempotency anahtarıyla işlenir.',
      },
      {
        q: 'Online kart ödemesi aktif mi?',
        a: 'Gerçek bir ödeme sağlayıcısı yapılandırılmadığında online ödeme güvenli biçimde kapalıdır. Sistem kart numarası veya CVV saklayan bir veri modeli içermez.',
      },
      {
        q: 'Yasal metinler nasıl yönetiliyor?',
        a: 'Checkout, yayındaki zorunlu ve sürümlü metinler yüklenmeden ve kullanıcı kabulü kaydedilmeden tamamlanmaz.',
      },
    ],
  },
  operasyon: {
    title: 'Teslimat ve İade',
    questions: [
      {
        q: 'Kısmi sevkiyat destekleniyor mu?',
        a: 'Evet. Sipariş kalemleri birden fazla sevkiyata bölünebilir ve her sevkiyat kontrollü durum komutlarıyla ilerletilir.',
      },
      {
        q: 'İade süreci nasıl ilerliyor?',
        a: 'Teslim edilmiş ürün için adet kontrollü RMA açılır; inceleme, kabul veya ret ve gerektiğinde doğrulanmış ödeme referansıyla iade akışı yürütülür.',
      },
    ],
  },
  guvenlik: {
    title: 'Güvenlik',
    questions: [
      {
        q: 'Yönetim işlemleri nasıl korunuyor?',
        a: 'Yetkiler rol ve politika bazında ayrılır. Kritik yönetim olayları append-only, SHA-256 zincirli audit kaydına yazılır.',
      },
      {
        q: 'Sağlayıcı anahtarları repository’de tutuluyor mu?',
        a: 'Hayır. Kimlik bilgileri ortam değişkenleri veya secret store üzerinden sağlanır; eksik yapılandırma başarılı işlem gibi gösterilmez.',
      },
    ],
  },
};

const FAQPage = () => {
  const [activeCategory, setActiveCategory] = useState('genel');

  return (
    <div className="info-page">
      <div className="container">
        <h1>Sıkça Sorulan Sorular</h1>

        <div className="info-content">
          <div className="faq-container">
            <div className="faq-categories">
              {Object.keys(faqCategories).map((key) => (
                <button
                  key={key}
                  className={`category-btn ${activeCategory === key ? 'active' : ''}`}
                  onClick={() => setActiveCategory(key)}
                >
                  {faqCategories[key].title}
                </button>
              ))}
            </div>

            <div className="faq-content">
              <h2>{faqCategories[activeCategory].title}</h2>
              {faqCategories[activeCategory].questions.map((item) => (
                <div key={item.q} className="faq-item">
                  <h3>{item.q}</h3>
                  <p>{item.a}</p>
                </div>
              ))}
            </div>
          </div>

          <section className="info-section">
            <h2>Başka bir sorunuz mu var?</h2>
            <p>Yapılandırılmış destek kanallarını görmek için iletişim sayfasını açın.</p>
            <Link to="/iletisim">İletişim sayfasına git</Link>
          </section>
        </div>
      </div>
    </div>
  );
};

export default FAQPage;
