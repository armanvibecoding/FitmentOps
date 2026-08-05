import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { useAuth } from '../context/AuthContext';
import { b2bAPI } from '../services/api';

const newIdempotencyKey = () => globalThis.crypto?.randomUUID?.()
  || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
const displayDate = (value) => value
  ? new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
  : '—';
const displayMoney = (value, currency = 'TRY') => new Intl.NumberFormat('tr-TR', {
  style: 'currency',
  currency,
}).format(value);

const B2bPage = () => {
  const { user, loading: authLoading } = useAuth();
  const navigate = useNavigate();
  const [application, setApplication] = useState(null);
  const [quotes, setQuotes] = useState([]);
  const [quoteDetails, setQuoteDetails] = useState({});
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState(null);
  const [applicationKey, setApplicationKey] = useState(newIdempotencyKey);
  const [quoteKey, setQuoteKey] = useState(newIdempotencyKey);
  const [applicationForm, setApplicationForm] = useState({
    companyName: '',
    taxNumber: '',
    contactName: user?.fullName || '',
    contactEmail: user?.email || '',
    contactPhone: user?.phone || '',
  });
  const [quoteLines, setQuoteLines] = useState([{ identifier: '', quantity: 1 }]);

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [applicationResult, quotesResult] = await Promise.allSettled([
        b2bAPI.getApplication(),
        b2bAPI.getQuotes(),
      ]);
      if (applicationResult.status === 'fulfilled') {
        setApplication(applicationResult.value.data);
      } else if (applicationResult.reason?.response?.status !== 404) {
        throw applicationResult.reason;
      }
      if (quotesResult.status === 'fulfilled') {
        setQuotes(quotesResult.value.data);
      } else {
        throw quotesResult.reason;
      }
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'B2B hesabı yüklenemedi.' });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (authLoading) return;
    if (!user) {
      navigate('/login');
      return;
    }
    setApplicationForm((current) => ({
      ...current,
      contactName: current.contactName || user.fullName || '',
      contactEmail: current.contactEmail || user.email || '',
      contactPhone: current.contactPhone || user.phone || '',
    }));
    loadData();
  }, [authLoading, loadData, navigate, user]);

  const submitApplication = async (event) => {
    event.preventDefault();
    setBusy('application');
    setMessage(null);
    try {
      await b2bAPI.submitApplication(applicationForm, applicationKey);
      setApplicationKey(newIdempotencyKey());
      setApplicationForm((current) => ({ ...current, taxNumber: '' }));
      await loadData();
      setMessage({ type: 'success', text: 'Bayi başvurunuz kaydedildi.' });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Başvuru gönderilemedi.' });
    } finally {
      setBusy('');
    }
  };

  const updateQuoteLine = (index, changes) => {
    setQuoteLines((current) => current.map((line, lineIndex) => (
      lineIndex === index ? { ...line, ...changes } : line
    )));
  };

  const submitQuote = async (event) => {
    event.preventDefault();
    setBusy('quote');
    setMessage(null);
    try {
      await b2bAPI.submitQuote({
        lines: quoteLines.map((line) => ({
          identifier: line.identifier,
          quantity: Number(line.quantity),
        })),
      }, quoteKey);
      setQuoteKey(newIdempotencyKey());
      setQuoteLines([{ identifier: '', quantity: 1 }]);
      await loadData();
      setMessage({ type: 'success', text: 'Toplu teklif talebiniz alındı.' });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Teklif talebi gönderilemedi.' });
    } finally {
      setBusy('');
    }
  };

  const loadQuoteDetail = async (id) => {
    if (quoteDetails[id]) {
      setQuoteDetails((current) => ({ ...current, [id]: null }));
      return;
    }
    setBusy(`detail-${id}`);
    try {
      const response = await b2bAPI.getQuote(id);
      setQuoteDetails((current) => ({ ...current, [id]: response.data }));
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Teklif detayı yüklenemedi.' });
    } finally {
      setBusy('');
    }
  };

  const acceptQuote = async (id) => {
    if (!window.confirm('Bu kurumsal teklifi kabul etmek istediğinizden emin misiniz?')) return;
    setBusy(`accept-${id}`);
    try {
      await b2bAPI.acceptQuote(id);
      await loadData();
      setMessage({ type: 'success', text: 'Teklif kabul edildi.' });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Teklif kabul edilemedi.' });
    } finally {
      setBusy('');
    }
  };

  if (authLoading || loading) return <div className="container"><p>Kurumsal hesap yükleniyor...</p></div>;
  if (!user) return null;

  return (
    <div className="container" style={{ padding: '40px 20px' }}>
      <div className="management-header">
        <div>
          <h1>Kurumsal müşteri merkezi</h1>
          <p>Bayi başvurunuzu, toplu fiyat taleplerinizi ve geçerli tekliflerinizi yönetin.</p>
        </div>
        <button type="button" className="add-button" onClick={loadData} disabled={Boolean(busy)}>Yenile</button>
      </div>

      {message && <div className={`admin-operation-message ${message.type}`}>{message.text}</div>}

      {!application ? (
        <form className="admin-catalog-form" onSubmit={submitApplication}>
          <h2>Bayi başvurusu</h2>
          <p>Başvuru şirket ve vergi bilgileriyle manuel olarak doğrulanır. Vergi numarası özet ekranlarda maskelenir.</p>
          <div className="catalog-form-grid">
            <label>Şirket unvanı<input required minLength="2" maxLength="160" value={applicationForm.companyName} onChange={(event) => setApplicationForm({ ...applicationForm, companyName: event.target.value })} /></label>
            <label>Vergi numarası<input required minLength="5" maxLength="32" autoComplete="off" value={applicationForm.taxNumber} onChange={(event) => setApplicationForm({ ...applicationForm, taxNumber: event.target.value })} /></label>
            <label>Yetkili kişi<input required minLength="2" maxLength="100" value={applicationForm.contactName} onChange={(event) => setApplicationForm({ ...applicationForm, contactName: event.target.value })} /></label>
            <label>E-posta<input required type="email" maxLength="200" value={applicationForm.contactEmail} onChange={(event) => setApplicationForm({ ...applicationForm, contactEmail: event.target.value })} /></label>
            <label>Telefon<input required type="tel" maxLength="20" value={applicationForm.contactPhone} onChange={(event) => setApplicationForm({ ...applicationForm, contactPhone: event.target.value })} /></label>
          </div>
          <button type="submit" className="add-button" disabled={Boolean(busy)}>Başvuruyu gönder</button>
        </form>
      ) : (
        <section className="admin-catalog-form">
          <h2>{application.companyName}</h2>
          <div className="operation-card-grid">
            <div className="operation-card"><strong>Başvuru durumu</strong><span>{application.status}</span></div>
            <div className="operation-card"><strong>Müşteri grubu</strong><span>{application.customerGroup || 'Henüz atanmadı'}</span></div>
            <div className="operation-card"><strong>Vergi numarası</strong><span>•••• {application.taxNumberLast4}</span></div>
            <div className="operation-card"><strong>Başvuru tarihi</strong><span>{displayDate(application.createdAtUtc)}</span></div>
          </div>
          {application.status !== 'Approved' && <p className="admin-safety-note">RFQ göndermek için başvurunun admin tarafından onaylanması gerekir.</p>}
        </section>
      )}

      {application?.status === 'Approved' && (
        <form className="admin-catalog-form" onSubmit={submitQuote}>
          <h2>Toplu teklif talebi oluştur</h2>
          <p>OEM, ürün veya doğrulanmış çapraz referans kodu ile en fazla 500 kalem gönderebilirsiniz.</p>
          {quoteLines.map((line, index) => (
            <div className="catalog-form-grid" key={`${index}-${quoteLines.length}`}>
              <label>Parça kodu<input required maxLength="80" value={line.identifier} onChange={(event) => updateQuoteLine(index, { identifier: event.target.value })} /></label>
              <label>Adet<input required type="number" min="1" max="100000" value={line.quantity} onChange={(event) => updateQuoteLine(index, { quantity: event.target.value })} /></label>
              {quoteLines.length > 1 && <button type="button" className="delete-btn" onClick={() => setQuoteLines((current) => current.filter((_, lineIndex) => lineIndex !== index))}>Satırı kaldır</button>}
            </div>
          ))}
          <div className="form-actions">
            <button type="button" className="cancel-button" onClick={() => setQuoteLines((current) => [...current, { identifier: '', quantity: 1 }])} disabled={quoteLines.length >= 500}>Satır ekle</button>
            <button type="submit" className="save-button" disabled={Boolean(busy)}>RFQ gönder</button>
          </div>
        </form>
      )}

      <section className="admin-catalog-form">
        <h2>Teklif taleplerim</h2>
        <div className="orders-table operations-table payments-table">
          <table>
            <thead><tr><th>Talep</th><th>Durum</th><th>Kalem</th><th>Geçerlilik</th><th>İşlem</th></tr></thead>
            <tbody>
              {quotes.map((quote) => (
                <tr key={quote.id}>
                  <td>{quote.requestNumber}<br /><small>{displayDate(quote.createdAtUtc)}</small></td>
                  <td>{quote.status}</td>
                  <td>
                    {quote.lineCount}
                    {quoteDetails[quote.id]?.lines.map((line) => (
                      <div key={line.id}>
                        {line.requestedIdentifier} × {line.requestedQuantity} — {line.status}
                        {line.quotedUnitPrice != null ? ` / ${displayMoney(line.quotedUnitPrice, quote.currency)}` : ''}
                      </div>
                    ))}
                  </td>
                  <td>{displayDate(quote.quoteValidUntilUtc)}</td>
                  <td>
                    <button type="button" className="edit-btn" onClick={() => loadQuoteDetail(quote.id)} disabled={Boolean(busy)}>{quoteDetails[quote.id] ? 'Kapat' : 'Detay'}</button>
                    {quote.status === 'Quoted' && <button type="button" className="add-button" onClick={() => acceptQuote(quote.id)} disabled={Boolean(busy)}>Kabul et</button>}
                  </td>
                </tr>
              ))}
              {quotes.length === 0 && <tr><td colSpan="5">Henüz teklif talebiniz yok.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
};

export default B2bPage;
