import { useState } from 'react';
import { brand } from '../config/brand';
import './InfoPages.css';

const initialForm = {
  name: '',
  email: '',
  subject: '',
  message: '',
};

const ContactPage = () => {
  const [formData, setFormData] = useState(initialForm);

  const handleChange = (event) => {
    setFormData((current) => ({
      ...current,
      [event.target.name]: event.target.value,
    }));
  };

  const handleSubmit = (event) => {
    event.preventDefault();
    if (!brand.supportEmail) return;

    const subject = encodeURIComponent(`[${brand.name}] ${formData.subject}`);
    const body = encodeURIComponent(
      `Ad Soyad: ${formData.name}\nE-posta: ${formData.email}\n\n${formData.message}`
    );
    window.location.href = `mailto:${brand.supportEmail}?subject=${subject}&body=${body}`;
  };

  return (
    <div className="info-page">
      <div className="container">
        <h1>İletişim</h1>

        <div className="info-content">
          <div className="contact-grid">
            <div className="contact-info">
              <h2>Destek kanalları</h2>

              {brand.supportEmail ? (
                <div className="contact-item">
                  <div className="contact-icon">✉️</div>
                  <div>
                    <h3>E-posta</h3>
                    <a href={`mailto:${brand.supportEmail}`}>{brand.supportEmail}</a>
                  </div>
                </div>
              ) : (
                <p>
                  Bu ortam için destek e-postası henüz yapılandırılmamıştır.
                  Operatör, <code>VITE_SUPPORT_EMAIL</code> değerini tanımladığında
                  iletişim formu etkinleşir.
                </p>
              )}

              {brand.supportPhone && (
                <div className="contact-item">
                  <div className="contact-icon">📞</div>
                  <div>
                    <h3>Telefon</h3>
                    <p>{brand.supportPhone}</p>
                  </div>
                </div>
              )}

              {brand.businessAddress && (
                <div className="contact-item">
                  <div className="contact-icon">📍</div>
                  <div>
                    <h3>Adres</h3>
                    <p>{brand.businessAddress}</p>
                  </div>
                </div>
              )}
            </div>

            <div className="contact-form-container">
              <h2>Destek e-postası oluştur</h2>
              <form className="contact-form" onSubmit={handleSubmit}>
                <div className="form-group">
                  <label htmlFor="name">Ad Soyad *</label>
                  <input id="name" name="name" value={formData.name} onChange={handleChange} required />
                </div>

                <div className="form-group">
                  <label htmlFor="email">E-posta *</label>
                  <input type="email" id="email" name="email" value={formData.email} onChange={handleChange} required />
                </div>

                <div className="form-group">
                  <label htmlFor="subject">Konu *</label>
                  <input id="subject" name="subject" value={formData.subject} onChange={handleChange} required />
                </div>

                <div className="form-group">
                  <label htmlFor="message">Mesajınız *</label>
                  <textarea id="message" name="message" rows="5" value={formData.message} onChange={handleChange} required />
                </div>

                <button type="submit" className="submit-btn" disabled={!brand.supportEmail}>
                  E-posta oluştur
                </button>
                <p className="text-small">
                  Form verileri sunucuya gönderilmez; cihazınızdaki e-posta uygulamasında taslak oluşturur.
                </p>
              </form>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};

export default ContactPage;
