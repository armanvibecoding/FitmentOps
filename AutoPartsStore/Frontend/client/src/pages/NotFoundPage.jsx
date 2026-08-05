import { Link } from 'react-router';
import './NotFoundPage.css';

const NotFoundPage = () => {
  return (
    <div className="not-found-page">
      <div className="not-found-container">
        <div className="not-found-content">
          <h1 className="error-code">404</h1>
          <h2 className="error-title">Sayfa Bulunamadı</h2>
          <p className="error-message">
            Aradığınız sayfa mevcut değil veya taşınmış olabilir.
          </p>
          <div className="error-actions">
            <Link to="/" className="btn-home">
              Ana Sayfaya Dön
            </Link>
            <Link to="/products" className="btn-products">
              Ürünlere Göz At
            </Link>
          </div>
        </div>
        <div className="error-illustration">
          <svg viewBox="0 0 500 500" xmlns="http://www.w3.org/2000/svg">
            <circle cx="250" cy="250" r="200" fill="#f0f0f0"/>
            <text x="250" y="280" fontSize="120" textAnchor="middle" fill="#666">404</text>
          </svg>
        </div>
      </div>
    </div>
  );
};

export default NotFoundPage;
