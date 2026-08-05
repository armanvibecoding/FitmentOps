import { Link } from 'react-router';
import { brand } from '../config/brand';
import './InfoPages.css';

const ReturnsPage = () => {
  return (
    <div className="info-page">
      <div className="container">
        <h1>İade ve RMA Süreci</h1>

        <div className="info-content">
          <section className="info-section">
            <h2>Kontrollü satış sonrası operasyon</h2>
            <p>
              {brand.name}, teslim edilmiş sipariş kalemleri için adet kontrollü
              iade talebi, inceleme ve sonuçlandırma akışı sağlar. Bağlayıcı iade
              koşulları checkout sırasında kabul edilen güncel ve sürümlü yasal
              metinlerdir.
            </p>
          </section>

          <section className="info-section">
            <h2>Süreç</h2>
            <div className="process-steps">
              <div className="process-step">
                <div className="step-number">1</div>
                <h3>Talep</h3>
                <p>Teslim edilen kalem ve adet için tekil RMA talebi oluşturulur.</p>
              </div>
              <div className="process-step">
                <div className="step-number">2</div>
                <h3>Değerlendirme</h3>
                <p>Talep nedeni, sipariş durumu ve daha önce iade edilen adetler doğrulanır.</p>
              </div>
              <div className="process-step">
                <div className="step-number">3</div>
                <h3>Teslim alma</h3>
                <p>Onaylanan ürün operasyon ekibi tarafından teslim alınır.</p>
              </div>
              <div className="process-step">
                <div className="step-number">4</div>
                <h3>İnceleme</h3>
                <p>Ürün ve talep kanıtları kayıt altına alınarak sonuçlandırılır.</p>
              </div>
              <div className="process-step">
                <div className="step-number">5</div>
                <h3>İade veya kapanış</h3>
                <p>Uygun talep, doğrulanmış ödeme referansıyla kısmi veya tam iadeye bağlanır.</p>
              </div>
            </div>
          </section>

          <section className="info-section">
            <h2>Uyumluluk uyuşmazlıkları</h2>
            <p>
              Araç uyumluluğuyla ilgili taleplerde seçilen araç konfigürasyonu,
              gösterilen güven bandı, OEM kodu ve katalog kaynağı inceleme
              kanıtının parçasıdır. <strong>Unknown</strong> sonucu olumlu uyumluluk
              garantisi değildir.
            </p>
          </section>

          <section className="info-section">
            <h2>Destek</h2>
            <p>Yayın ortamında tanımlanmış iletişim kanalını kullanarak sipariş numaranızla destek alın.</p>
            <Link to="/iletisim">İletişim sayfasına git</Link>
          </section>
        </div>
      </div>
    </div>
  );
};

export default ReturnsPage;
