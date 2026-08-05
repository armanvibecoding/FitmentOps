import { useCallback, useEffect, useState } from 'react';
import { adminAPI } from '../services/api';

const formatDateTime = (value) => new Intl.DateTimeFormat('tr-TR', {
  dateStyle: 'short', timeStyle: 'medium',
}).format(new Date(value));

const AuditAdminPanel = () => {
  const [events, setEvents] = useState([]);
  const [verification, setVerification] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const [eventsResponse, verifyResponse] = await Promise.all([
        adminAPI.getAuditEvents({ pageSize: 100 }),
        adminAPI.verifyAuditChain(),
      ]);
      setEvents(eventsResponse.data);
      setVerification(verifyResponse.data);
    } catch (requestError) {
      setError(requestError.response?.data?.message || 'Audit kayıtları doğrulanamadı.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  return (
    <div className="audit-management">
      <div className="management-header"><h1>Yönetim audit zinciri</h1><button type="button" className="add-button" onClick={load} disabled={loading}>{loading ? 'Doğrulanıyor…' : 'Yenile ve doğrula'}</button></div>
      {error && <div className="admin-operation-message error" role="alert">{error}</div>}
      {verification && <div className={`audit-verification ${verification.isValid ? 'valid' : 'invalid'}`} role="status"><strong>{verification.isValid ? 'Zincir bütünlüğü geçerli' : 'Zincir bütünlüğü bozuk'}</strong><span>{verification.verifiedEventCount} olay doğrulandı.</span></div>}
      <div className="orders-table operations-table">
        <table>
          <thead><tr><th>Sıra</th><th>Zaman</th><th>Aktör</th><th>Eylem</th><th>Varlık</th><th>Sonuç</th></tr></thead>
          <tbody>{events.map((event) => <tr key={event.sequence}><td>{event.sequence}</td><td>{formatDateTime(event.occurredAtUtc)}</td><td>#{event.actorUserId} · {event.actorRole}</td><td>{event.action}</td><td>{event.aggregateType} #{event.aggregateId}</td><td>{event.outcome}</td></tr>)}</tbody>
        </table>
      </div>
      {!loading && events.length === 0 && <p>Henüz audit olayı bulunmuyor.</p>}
      <p className="admin-safety-note">Bu ekran yalnız metadata gösterir; ham correlation/idempotency değerleri ve işlem payload’ları saklanmaz.</p>
    </div>
  );
};

export default AuditAdminPanel;
