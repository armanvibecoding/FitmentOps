import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { useCart } from '../context/CartContext';
import { legalAPI, ordersAPI, paymentsAPI } from '../services/api';

const PAYMENT_METHOD_LABELS = {
  PayAtDelivery: 'Teslimatta ödeme',
  HostedCard: 'Güvenli online kart ödemesi',
};

const PAYMENT_STATUS_LABELS = {
  Pending: 'Ödeme teslimatta tahsil edilecek',
  Paid: 'Ödendi',
  Failed: 'Ödeme başarısız',
  Cancelled: 'Ödeme iptal edildi',
  PartiallyRefunded: 'Kısmen iade edildi',
  Refunded: 'İade edildi',
  Unknown: 'Sağlayıcı sonucu bekleniyor',
};

const formatCurrency = (amount, currency = 'TRY') =>
  new Intl.NumberFormat('tr-TR', {
    style: 'currency',
    currency,
  }).format(amount);

const CheckoutPage = () => {
  const { cart, getCartTotal, clearCart } = useCart();
  const [idempotencyKey] = useState(() => crypto.randomUUID());
  const [checkoutResult, setCheckoutResult] = useState(null);
  const [onlineCardEnabled, setOnlineCardEnabled] = useState(false);
  const [legalDocuments, setLegalDocuments] = useState([]);
  const [legalAcceptances, setLegalAcceptances] = useState({});
  const [legalLoading, setLegalLoading] = useState(true);
  const [paymentMethod, setPaymentMethod] = useState('PayAtDelivery');
  const [formData, setFormData] = useState({
    customerName: '',
    customerEmail: '',
    customerPhone: '',
    shippingAddress: '',
    city: '',
    postalCode: '',
    identityNumber: '',
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    paymentsAPI.getCapabilities()
      .then((response) => {
        if (active) setOnlineCardEnabled(Boolean(response.data?.onlineCard));
      })
      .catch(() => {
        if (active) setOnlineCardEnabled(false);
      });

    legalAPI.getCheckoutDocuments()
      .then((response) => {
        if (active) {
          setLegalDocuments(Array.isArray(response.data) ? response.data : []);
          setLegalLoading(false);
        }
      })
      .catch(() => {
        if (active) {
          setLegalDocuments([]);
          setLegalLoading(false);
          setError('Sipariş için gerekli güncel yasal metinler henüz yayınlanmadı.');
        }
      });

    return () => {
      active = false;
    };
  }, []);

  const handleChange = (event) => {
    setFormData({
      ...formData,
      [event.target.name]: event.target.value,
    });
  };

  const handleSubmit = async (event) => {
    event.preventDefault();

    if (loading || cart.length === 0) return;

    if (legalDocuments.length === 0 ||
        legalDocuments.some((document) => !legalAcceptances[document.documentType])) {
      setError('Sipariş vermeden önce güncel zorunlu metinlerin tamamını okuyup kabul edin.');
      return;
    }

    setLoading(true);
    setError('');

    try {
      const items = cart.map((item) => ({
          productId: item.id,
          quantity: item.quantity,
        }));
      const acceptancePayload = legalDocuments.map((document) => ({
        documentType: document.documentType,
        version: document.version,
        contentSha256: document.contentSha256,
        accepted: Boolean(legalAcceptances[document.documentType]),
      }));
      let response;
      if (paymentMethod === 'HostedCard') {
        const nameParts = formData.customerName.trim().split(/\s+/);
        const firstName = nameParts.shift() || '';
        const lastName = nameParts.join(' ') || '-';
        response = await ordersAPI.startHostedCheckout({
          firstName,
          lastName,
          email: formData.customerEmail,
          phone: formData.customerPhone,
          identityNumber: formData.identityNumber,
          shippingAddress: formData.shippingAddress,
          city: formData.city,
          postalCode: formData.postalCode,
          items,
          legalAcceptances: acceptancePayload,
        }, idempotencyKey);

        if (response.data?.redirectUri) {
          clearCart();
          window.location.assign(response.data.redirectUri);
          return;
        }

        setCheckoutResult({
          ...response.data,
          totalAmount: getCartTotal(),
          currency: 'TRY',
          paymentMethod: 'HostedCard',
        });
      } else {
        response = await ordersAPI.create({
          customerName: formData.customerName,
          customerEmail: formData.customerEmail,
          customerPhone: formData.customerPhone,
          shippingAddress: formData.shippingAddress,
          city: formData.city,
          postalCode: formData.postalCode,
          paymentMethod: 'PayAtDelivery',
          items,
          legalAcceptances: acceptancePayload,
        }, idempotencyKey);
        setCheckoutResult(response.data);
      }
      clearCart();
    } catch (requestError) {
      setError(
        requestError.response?.data?.message ||
          'Sipariş oluşturulurken bir hata oluştu. Aynı bilgilerle yeniden deneyebilirsiniz.'
      );
    } finally {
      setLoading(false);
    }
  };

  if (checkoutResult) {
    const trackingUrl = `/siparis-takibi?orderNumber=${encodeURIComponent(
      checkoutResult.orderNumber
    )}`;

    return (
      <div className="container cart-page">
        <div
          aria-live="polite"
          style={{
            maxWidth: '720px',
            margin: '40px auto',
            padding: '30px',
            backgroundColor: 'white',
            border: '1px solid #bbf7d0',
            borderRadius: '8px',
          }}
        >
          <h1 className="section-title">Siparişiniz oluşturuldu</h1>
          <p>
            Sipariş numaranızı saklayın. Takip ekranında bu numara ve siparişte
            kullandığınız e-posta adresi istenecektir.
          </p>

          <dl
            style={{
              display: 'grid',
              gridTemplateColumns: 'minmax(160px, 1fr) 2fr',
              gap: '12px 20px',
              margin: '24px 0',
            }}
          >
            <dt>Sipariş numarası</dt>
            <dd><strong>{checkoutResult.orderNumber}</strong></dd>
            <dt>Toplam</dt>
            <dd>{formatCurrency(checkoutResult.totalAmount, checkoutResult.currency)}</dd>
            <dt>Ödeme yöntemi</dt>
            <dd>
              {PAYMENT_METHOD_LABELS[checkoutResult.paymentMethod] || 'Belirtilmedi'}
            </dd>
            <dt>Ödeme durumu</dt>
            <dd>
              {PAYMENT_STATUS_LABELS[checkoutResult.paymentStatus] ||
                'Durum bilgisi mevcut değil'}
            </dd>
          </dl>

          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '12px' }}>
            <Link className="checkout-button" style={{ width: 'auto', textDecoration: 'none' }} to={trackingUrl}>
              Siparişi takip et
            </Link>
            <Link to="/">Alışverişe dön</Link>
          </div>
        </div>
      </div>
    );
  }

  if (cart.length === 0) {
    return (
      <div className="container">
        <div className="empty-cart">
          <h2>Sepetiniz boş!</h2>
          <p>Sipariş vermek için önce sepetinize ürün ekleyin.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="container cart-page">
      <h1 className="section-title">Sipariş Bilgileri</h1>

      {error && (
        <div
          role="alert"
          style={{ color: '#991b1b', marginBottom: '20px', padding: '10px', border: '1px solid #fecaca', backgroundColor: '#fef2f2' }}
        >
          {error}
        </div>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: '30px' }}>
        <form onSubmit={handleSubmit} style={{ backgroundColor: 'white', padding: '30px', borderRadius: '8px', border: '1px solid #e0e0e0' }}>
          <h2 style={{ marginBottom: '20px' }}>İletişim Bilgileri</h2>

          <div style={{ marginBottom: '20px' }}>
            <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
              Ad Soyad *
            </label>
            <input
              type="text"
              name="customerName"
              value={formData.customerName}
              onChange={handleChange}
              required
              style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
            />
          </div>

          <div style={{ marginBottom: '20px' }}>
            <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
              E-posta *
            </label>
            <input
              type="email"
              name="customerEmail"
              value={formData.customerEmail}
              onChange={handleChange}
              required
              style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
            />
          </div>

          <div style={{ marginBottom: '20px' }}>
            <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
              Telefon *
            </label>
            <input
              type="tel"
              name="customerPhone"
              value={formData.customerPhone}
              onChange={handleChange}
              required
              style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
            />
          </div>

          <h2 style={{ marginBottom: '20px', marginTop: '30px' }}>Teslimat Adresi</h2>

          <div style={{ marginBottom: '20px' }}>
            <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
              Adres *
            </label>
            <textarea
              name="shippingAddress"
              value={formData.shippingAddress}
              onChange={handleChange}
              required
              rows="3"
              style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
            />
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '20px' }}>
            <div style={{ marginBottom: '20px' }}>
              <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
                Şehir *
              </label>
              <input
                type="text"
                name="city"
                value={formData.city}
                onChange={handleChange}
                required
                style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
              />
            </div>

            <div style={{ marginBottom: '20px' }}>
              <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
                Posta Kodu *
              </label>
              <input
                type="text"
                name="postalCode"
                value={formData.postalCode}
                onChange={handleChange}
                required
                style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
              />
            </div>
          </div>

          <div style={{ marginTop: '20px', padding: '14px', backgroundColor: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: '6px' }}>
            <strong>Ödeme yöntemi</strong>
            <label style={{ display: 'block', marginTop: '10px' }}>
              <input
                type="radio"
                name="paymentMethod"
                value="PayAtDelivery"
                checked={paymentMethod === 'PayAtDelivery'}
                onChange={(event) => setPaymentMethod(event.target.value)}
              />{' '}
              Teslimatta ödeme
            </label>
            {onlineCardEnabled && (
              <label style={{ display: 'block', marginTop: '8px' }}>
                <input
                  type="radio"
                  name="paymentMethod"
                  value="HostedCard"
                  checked={paymentMethod === 'HostedCard'}
                  onChange={(event) => setPaymentMethod(event.target.value)}
                />{' '}
                Güvenli online kart ödemesi
              </label>
            )}
            <p style={{ margin: '8px 0 0' }}>
              Kart numarası, son kullanma tarihi ve güvenlik kodu yalnızca ödeme
              sağlayıcısının güvenli sayfasında girilir.
            </p>
          </div>

          {paymentMethod === 'HostedCard' && (
            <div style={{ marginTop: '20px' }}>
              <label style={{ display: 'block', marginBottom: '5px', fontWeight: '600' }}>
                Kimlik / vergi kimlik numarası *
              </label>
              <input
                type="text"
                name="identityNumber"
                value={formData.identityNumber}
                onChange={handleChange}
                required
                minLength="5"
                maxLength="32"
                autoComplete="off"
                style={{ width: '100%', padding: '12px', border: '1px solid #e0e0e0', borderRadius: '4px' }}
              />
              <small>Bu bilgi sipariş tablosuna kaydedilmez; sağlayıcı başlatma isteğinde kullanılır.</small>
            </div>
          )}

          <section style={{ marginTop: '20px', padding: '14px', backgroundColor: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: '6px' }}>
            <strong>Yasal bilgilendirme ve sözleşmeler</strong>
            {legalLoading && <p>Güncel metinler yükleniyor…</p>}
            {!legalLoading && legalDocuments.length === 0 && (
              <p role="alert">Zorunlu metinler yayınlanmadan sipariş oluşturulamaz.</p>
            )}
            {legalDocuments.map((document) => (
              <div key={`${document.documentType}-${document.version}`} style={{ marginTop: '12px' }}>
                <details>
                  <summary>{document.title} — sürüm {document.version}</summary>
                  <div style={{ whiteSpace: 'pre-wrap', maxHeight: '260px', overflow: 'auto', marginTop: '8px', padding: '10px', background: 'white', border: '1px solid #e2e8f0' }}>
                    {document.content}
                  </div>
                </details>
                <label style={{ display: 'flex', gap: '8px', alignItems: 'flex-start', marginTop: '8px' }}>
                  <input
                    type="checkbox"
                    checked={Boolean(legalAcceptances[document.documentType])}
                    onChange={(event) => setLegalAcceptances((current) => ({
                      ...current,
                      [document.documentType]: event.target.checked,
                    }))}
                  />
                  <span>{document.title} metninin sürüm {document.version} içeriğini okudum ve kabul ediyorum.</span>
                </label>
              </div>
            ))}
          </section>

          <button
            type="submit"
            className="checkout-button"
            disabled={loading || legalLoading || legalDocuments.length === 0 ||
              legalDocuments.some((document) => !legalAcceptances[document.documentType])}
            style={{ marginTop: '20px', opacity: loading ? 0.65 : 1 }}
          >
            {loading
              ? 'İşleniyor…'
              : paymentMethod === 'HostedCard'
                ? 'Güvenli Ödemeye Geç'
                : 'Siparişi Oluştur (Teslimatta Ödeme)'}
          </button>
        </form>

        <div>
          <div className="cart-summary">
            <h2 style={{ marginBottom: '20px' }}>Sipariş Özeti</h2>

            {cart.map((item) => (
              <div key={item.id} style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '10px', paddingBottom: '10px', borderBottom: '1px solid #e0e0e0' }}>
                <span>{item.name} x{item.quantity}</span>
                <span>{(item.price * item.quantity).toFixed(2)} TL</span>
              </div>
            ))}

            <div className="cart-total" style={{ marginTop: '20px', paddingTop: '20px', borderTop: '2px solid #e0e0e0' }}>
              <span>Toplam:</span>
              <span>{getCartTotal().toFixed(2)} TL</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};

export default CheckoutPage;
