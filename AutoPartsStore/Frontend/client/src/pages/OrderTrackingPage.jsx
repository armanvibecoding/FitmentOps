import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { ordersAPI } from '../services/api';
import './InfoPages.css';

const STATUS_LABELS = {
  Pending: 'Sipariş alındı',
  Processing: 'Hazırlanıyor',
  Shipped: 'Kargoya verildi',
  Delivered: 'Teslim edildi',
  Cancelled: 'İptal edildi',
};

const TRACKING_STEPS = ['Pending', 'Processing', 'Shipped', 'Delivered'];

const PAYMENT_STATUS_LABELS = {
  Pending: 'Ödeme bekleniyor',
  Paid: 'Ödendi',
  Failed: 'Ödeme başarısız',
  Cancelled: 'Ödeme iptal edildi',
  PartiallyRefunded: 'Kısmen iade edildi',
  Refunded: 'İade edildi',
};

const formatCurrency = (value) =>
  new Intl.NumberFormat('tr-TR', {
    style: 'currency',
    currency: 'TRY',
  }).format(value);

const formatDate = (value) =>
  new Intl.DateTimeFormat('tr-TR', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value));

const OrderTrackingPage = () => {
  const [searchParams] = useSearchParams();
  const [orderNumber, setOrderNumber] = useState(() =>
    (searchParams.get('orderNumber') || '').trim().slice(0, 50)
  );
  const [email, setEmail] = useState('');
  const [orderStatus, setOrderStatus] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleSubmit = async (event) => {
    event.preventDefault();
    setLoading(true);
    setError('');
    setOrderStatus(null);

    try {
      const response = await ordersAPI.track(orderNumber.trim(), email.trim());
      setOrderStatus(response.data);
    } catch (requestError) {
      setError(
        requestError.response?.data?.message ||
          'Sipariş bilgileri alınamadı. Lütfen daha sonra tekrar deneyin.'
      );
    } finally {
      setLoading(false);
    }
  };

  const currentStepIndex = orderStatus
    ? TRACKING_STEPS.indexOf(orderStatus.status)
    : -1;

  return (
    <div className="info-page">
      <div className="container">
        <h1>Sipariş Takibi</h1>

        <div className="info-content">
          <section className="info-section">
            <h2>Siparişinizi Takip Edin</h2>
            <p>
              Sipariş numaranız ve siparişte kullandığınız e-posta adresiyle
              güncel durumu görüntüleyebilirsiniz.
            </p>

            <form className="tracking-form" onSubmit={handleSubmit}>
              <div className="form-group">
                <label htmlFor="orderNumber">Sipariş Numarası *</label>
                <input
                  type="text"
                  id="orderNumber"
                  value={orderNumber}
                  onChange={(event) => setOrderNumber(event.target.value)}
                  placeholder="Örn: ORD-638900000000000000"
                  maxLength={50}
                  autoComplete="off"
                  required
                />
              </div>

              <div className="form-group">
                <label htmlFor="email">E-posta Adresi *</label>
                <input
                  type="email"
                  id="email"
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  placeholder="ornek@email.com"
                  maxLength={200}
                  autoComplete="email"
                  required
                />
              </div>

              {error && (
                <p className="tracking-error" role="alert">
                  {error}
                </p>
              )}

              <button type="submit" className="submit-btn" disabled={loading}>
                {loading ? 'Sorgulanıyor…' : 'Sorgula'}
              </button>
            </form>
          </section>

          {orderStatus && (
            <section className="info-section order-result" aria-live="polite">
              <h2>Sipariş Detayları</h2>

              <div className="order-info-grid">
                <div className="order-info-item">
                  <strong>Sipariş No:</strong> {orderStatus.orderNumber}
                </div>
                <div className="order-info-item">
                  <strong>Sipariş Tarihi:</strong> {formatDate(orderStatus.orderDate)}
                </div>
                <div className="order-info-item">
                  <strong>Durum:</strong>{' '}
                  <span className="status-badge">
                    {STATUS_LABELS[orderStatus.status] || orderStatus.status}
                  </span>
                </div>
                <div className="order-info-item">
                  <strong>Ödeme:</strong>{' '}
                  {PAYMENT_STATUS_LABELS[orderStatus.paymentStatus] ||
                    'Durum bilgisi mevcut değil'}
                </div>
              </div>

              {currentStepIndex >= 0 && (
                <div className="tracking-timeline">
                  <h3>Sipariş Durumu</h3>
                  <div className="timeline">
                    {TRACKING_STEPS.map((step, index) => {
                      const completed = index <= currentStepIndex;
                      return (
                        <div
                          key={step}
                          className={`timeline-item ${completed ? 'completed' : 'pending'}`}
                        >
                          <div className="timeline-marker">
                            {completed ? '✓' : index + 1}
                          </div>
                          <div className="timeline-content">
                            <h4>{STATUS_LABELS[step]}</h4>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}

              <div className="order-items">
                <h3>Sipariş İçeriği</h3>
                <table className="items-table">
                  <thead>
                    <tr>
                      <th>Ürün</th>
                      <th>Adet</th>
                      <th>Birim fiyat</th>
                    </tr>
                  </thead>
                  <tbody>
                    {orderStatus.items.map((item, index) => (
                      <tr key={`${item.productName}-${index}`}>
                        <td>{item.productName}</td>
                        <td>{item.quantity}</td>
                        <td>{formatCurrency(item.unitPrice)}</td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr>
                      <td colSpan="2">
                        <strong>Toplam</strong>
                      </td>
                      <td>
                        <strong>{formatCurrency(orderStatus.totalAmount)}</strong>
                      </td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            </section>
          )}

          <section className="info-section">
            <h2>Sıkça Sorulan Sorular</h2>
            <div className="faq-item">
              <h3>Sipariş numaram nerede?</h3>
              <p>
                Siparişiniz oluşturulduğunda sipariş numarası ödeme/onay
                ekranında ve gönderilen e-postada yer alır.
              </p>
            </div>
            <div className="faq-item">
              <h3>Neden e-posta adresi isteniyor?</h3>
              <p>
                Sipariş bilgilerinizin yalnız doğru kişiye gösterilmesi için
                sipariş sırasında kullanılan e-posta adresini doğruluyoruz.
              </p>
            </div>
          </section>
        </div>
      </div>
    </div>
  );
};

export default OrderTrackingPage;
