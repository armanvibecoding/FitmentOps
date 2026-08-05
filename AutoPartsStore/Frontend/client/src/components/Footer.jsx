import { Link } from 'react-router';

const Footer = () => {
  return (
    <footer className="footer">
      <div className="container">
        <div className="footer-content">
          <div className="footer-section">
            <h3>Kurumsal</h3>
            <ul>
              <li><Link to="/hakkimizda">Hakkımızda</Link></li>
              <li><Link to="/iletisim">İletişim</Link></li>
              <li><Link to="/kariyer">Kariyer</Link></li>
            </ul>
          </div>

          <div className="footer-section">
            <h3>Müşteri Hizmetleri</h3>
            <ul>
              <li><Link to="/siparis-takibi">Sipariş Takibi</Link></li>
              <li><Link to="/iade-degisim">İade ve Değişim</Link></li>
              <li><Link to="/sss">Sıkça Sorulan Sorular</Link></li>
            </ul>
          </div>

          <div className="footer-section">
            <h3>Kategoriler</h3>
            <ul>
              <li><Link to="/category/motor-parcalari">Motor Parçaları</Link></li>
              <li><Link to="/category/fren-sistemi">Fren Sistemi</Link></li>
              <li><Link to="/category/filtreler">Filtreler</Link></li>
            </ul>
          </div>

          <div className="footer-section">
            <h3>İletişim</h3>
            <ul>
              <li>Telefon: 0850 123 45 67</li>
              <li>Email: info@parcamuhendisi.com</li>
              <li>Adres: İstanbul, Türkiye</li>
            </ul>
          </div>
        </div>

        <div className="footer-bottom">
          <p>&copy; 2025 Parça Mühendisi. Tüm hakları saklıdır.</p>
        </div>
      </div>
    </footer>
  );
};

export default Footer;
