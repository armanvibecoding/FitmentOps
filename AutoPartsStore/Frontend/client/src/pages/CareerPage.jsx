import { brand } from '../config/brand';
import './InfoPages.css';

const CareerPage = () => {
  return (
    <div className="info-page">
      <div className="container">
        <h1>Kariyer ve Katkı</h1>

        <div className="info-content">
          <section className="info-section">
            <h2>{brand.name} ile otomotiv ticaret altyapısı geliştirin</h2>
            <p>
              Proje; güvenli e-ticaret, araç uyumluluğu, operasyon otomasyonu ve
              gözlemlenebilirlik alanlarında çalışan geliştiricilere açık bir
              mühendislik zemini sunar.
            </p>
          </section>

          <section className="info-section">
            <h2>Açık pozisyonlar</h2>
            <p>
              Şu anda doğrulanmış bir açık pozisyon yayınlanmamıştır. Yeni bir
              pozisyon oluştuğunda rol, lokasyon, çalışma biçimi ve başvuru
              kanalı bu sayfada açıkça belirtilir.
            </p>
            {brand.careerEmail && (
              <p>
                Kariyer iletişimi: <a href={`mailto:${brand.careerEmail}`}>{brand.careerEmail}</a>
              </p>
            )}
          </section>

          <section className="info-section">
            <h2>Açık kaynak katkısı</h2>
            <p>
              Kod katkıları için repository içindeki katkı rehberini ve açık
              issue’ları inceleyebilirsiniz.
            </p>
            <a
              href="https://github.com/armanvibecoding/FitmentOps"
              target="_blank"
              rel="noreferrer"
            >
              GitHub repository’sini aç
            </a>
          </section>
        </div>
      </div>
    </div>
  );
};

export default CareerPage;
