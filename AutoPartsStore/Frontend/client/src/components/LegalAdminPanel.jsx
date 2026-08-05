import { useCallback, useEffect, useState } from 'react';
import { adminAPI } from '../services/api';

const TYPES = [
  ['PreliminaryInformation', 'Ön bilgilendirme formu'],
  ['DistanceSalesAgreement', 'Mesafeli satış sözleşmesi'],
  ['PrivacyNotice', 'Aydınlatma metni'],
];

const LegalAdminPanel = ({ role }) => {
  const [documents, setDocuments] = useState([]);
  const [message, setMessage] = useState(null);
  const [busy, setBusy] = useState(false);
  const [form, setForm] = useState({
    documentType: TYPES[0][0],
    version: '',
    title: '',
    content: '',
  });
  const normalizedRole = String(role || '').toLowerCase();
  const canPublish = normalizedRole === 'admin' || normalizedRole === 'superadmin';

  const refresh = useCallback(async () => {
    const response = await adminAPI.getLegalDocuments();
    setDocuments(response.data || []);
  }, []);

  useEffect(() => {
    refresh().catch(() => setMessage({ type: 'error', text: 'Yasal metinler yüklenemedi.' }));
  }, [refresh]);

  const createDraft = async (event) => {
    event.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      await adminAPI.createLegalDocument(form);
      setForm((current) => ({ ...current, version: '', title: '', content: '' }));
      await refresh();
      setMessage({ type: 'success', text: 'Değiştirilemez taslak sürüm oluşturuldu.' });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Taslak oluşturulamadı.' });
    } finally {
      setBusy(false);
    }
  };

  const transition = async (document, action) => {
    setBusy(true);
    setMessage(null);
    try {
      if (action === 'publish') {
        await adminAPI.publishLegalDocument(document.id, document.concurrencyToken);
      } else {
        await adminAPI.retireLegalDocument(document.id, document.concurrencyToken);
      }
      await refresh();
      setMessage({ type: 'success', text: action === 'publish' ? 'Sürüm yayınlandı.' : 'Sürüm emekliye ayrıldı; ilgili checkout fail-closed olacaktır.' });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Durum değiştirilemedi.' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="integrations-management">
      <h1>Yasal metin sürümleri</h1>
      <p className="admin-safety-note">Metinler hukuk onayından sonra yayınlanmalıdır. Yayındaki içerik değiştirilemez; değişiklik için yeni sürüm oluşturulur. Sistem kabul anındaki sürüm ve SHA-256 özetini siparişe bağlar.</p>
      {message && <p role="status" className={`admin-message ${message.type}`}>{message.text}</p>}
      {canPublish && (
        <form onSubmit={createDraft} className="operation-card" style={{ display: 'grid', gap: '10px', marginBottom: '20px' }}>
          <select value={form.documentType} onChange={(event) => setForm({ ...form, documentType: event.target.value })}>
            {TYPES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
          <input required maxLength="40" placeholder="Sürüm (örn. 2026-08-01)" value={form.version} onChange={(event) => setForm({ ...form, version: event.target.value })} />
          <input required maxLength="200" placeholder="Başlık" value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} />
          <textarea required maxLength="100000" rows="12" placeholder="Hukuk tarafından onaylanmış düz metin" value={form.content} onChange={(event) => setForm({ ...form, content: event.target.value })} />
          <button type="submit" disabled={busy}>Değiştirilemez taslak oluştur</button>
        </form>
      )}
      <div className="operation-card-grid">
        {documents.map((document) => (
          <article className="operation-card" key={document.id}>
            <strong>{document.title}</strong>
            <span>{document.documentType} · {document.version}</span>
            <span>Durum: {document.status}</span>
            <span>SHA-256: {document.contentSha256.slice(0, 16)}…</span>
            <details><summary>Metni görüntüle</summary><div style={{ whiteSpace: 'pre-wrap', maxHeight: '260px', overflow: 'auto' }}>{document.content}</div></details>
            {canPublish && document.status === 'Draft' && <button type="button" disabled={busy} onClick={() => transition(document, 'publish')}>Yayınla</button>}
            {canPublish && document.status === 'Published' && <button type="button" disabled={busy} onClick={() => transition(document, 'retire')}>Yayından kaldır</button>}
          </article>
        ))}
      </div>
    </section>
  );
};

export default LegalAdminPanel;
