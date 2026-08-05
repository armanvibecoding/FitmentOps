import { brand } from '../config/brand';
import './InfoPages.css';

const AboutPage = () => {
  return (
    <div className="info-page">
      <div className="container">
        <h1>Hakkımızda</h1>

        <div className="info-content">
          <section className="info-section">
            <h2>{brand.name}: doğru parçadan kontrollü operasyona</h2>
            <p>
              {brand.name}, otomotiv satış sonrası pazarı için araç uyumluluğu,
              ürün kataloğu, sipariş, ödeme, sevkiyat, iade ve B2B süreçlerini
              tek bir operasyon platformunda birleştiren açık kaynaklı bir
              yazılım projesidir.
            </p>
          </section>

          <section className="info-section">
            <h2>Ürün yaklaşımı</h2>
            <p>
              Platform, doğrulanmamış bir parçayı uyumlu göstermemeyi ve harici
              sağlayıcı yapılandırılmadığında işlemleri güvenli biçimde kapalı
              tutmayı temel alır. Fitment sonucu; katalog kanıtı, kaynak ve güven
              seviyesiyle birlikte değerlendirilir.
            </p>
          </section>

          <section className="info-section">
            <h2>Platform kapsamı</h2>
            <ul className="feature-list">
              <li>✓ Araç ağacı, OEM ve interchange kodlarıyla kanıtlı uyumluluk</li>
              <li>✓ Sunucu fiyatlı, idempotent checkout ve stok rezervasyonu</li>
              <li>✓ Ödeme, iade, sevkiyat ve RMA durum makineleri</li>
              <li>✓ Garaj, bakım günlüğü ve parça geçmişi</li>
              <li>✓ B2B fiyatlandırma, RFQ, tedarikçi ve satış kanalı yönetimi</li>
              <li>✓ Rol ayrımlı yönetim, audit zinciri ve operasyonel sağlık</li>
            </ul>
          </section>

          <section className="info-section">
            <h2>Yayın durumu</h2>
            <p>
              Bu sürüm üretim öncesi mühendislik aşamasındadır. Gerçek ödeme,
              e-belge, kargo ve pazaryeri işlemleri; sağlayıcı sözleşmeleri,
              hukuki kontroller ve sandbox sertifikasyonu tamamlanmadan
              etkinleştirilmez.
            </p>
          </section>
        </div>
      </div>
    </div>
  );
};

export default AboutPage;
