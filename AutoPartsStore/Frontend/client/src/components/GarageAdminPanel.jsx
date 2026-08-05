import { useEffect, useState } from 'react';
import { adminAPI } from '../services/api';

const formatDate = (value) => value
  ? new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium' }).format(new Date(value))
  : '—';

const GarageAdminPanel = () => {
  const [summary, setSummary] = useState(null);
  const [userId, setUserId] = useState('');
  const [vehicles, setVehicles] = useState([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState(null);

  useEffect(() => {
    adminAPI.getGarageSummary()
      .then((response) => setSummary(response.data))
      .catch(() => setMessage({ type: 'error', text: 'Garaj operasyon özeti yüklenemedi.' }));
  }, []);

  const search = async (event) => {
    event.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      const response = await adminAPI.getUserGarage(Number(userId));
      setVehicles(response.data);
      setMessage(response.data.length === 0
        ? { type: 'success', text: 'Bu kullanıcı için kayıtlı araç bulunamadı.' }
        : null);
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Kullanıcı garajı yüklenemedi.' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="garage-admin">
      <div className="management-header"><div><h1>Garaj ve bakım operasyonu</h1><p>Destek görünümü salt okunurdur; kullanıcının bakım günlüğü onun adına değiştirilmez.</p></div></div>
      {message && <div className={`admin-operation-message ${message.type}`} role={message.type === 'error' ? 'alert' : 'status'}>{message.text}</div>}
      {summary && (
        <section className="admin-catalog-form">
          <h2>Operasyon özeti</h2>
          <div className="operation-card-grid">
            <div className="operation-card"><strong>Aktif araç</strong><span>{summary.activeVehicles}</span></div>
            <div className="operation-card"><strong>Garaj kullanan müşteri</strong><span>{summary.usersWithVehicles}</span></div>
            <div className="operation-card"><strong>Açık hatırlatıcı</strong><span>{summary.openReminders}</span></div>
            <div className="operation-card"><strong>Vadesi gelen</strong><span>{summary.dueReminders}</span></div>
            <div className="operation-card"><strong>Son 30 gün bakım kaydı</strong><span>{summary.maintenanceRecordsInLastThirtyDays}</span></div>
          </div>
        </section>
      )}
      <form className="admin-catalog-form" onSubmit={search}>
        <h2>Müşteri garajı bul</h2>
        <p className="admin-safety-note">Sipariş veya destek kaydındaki kullanıcı ID ile arayın. Bu görünüm e-posta, telefon, plaka, VIN veya bakım notlarını açığa çıkarmaz.</p>
        <div className="catalog-form-grid"><label>Kullanıcı ID<input type="number" min="1" required value={userId} onChange={(event) => setUserId(event.target.value)} /></label></div>
        <button className="add-button" disabled={busy}>{busy ? 'Aranıyor…' : 'Garajı getir'}</button>
      </form>
      {vehicles.length > 0 && (
        <div className="orders-table operations-table">
          <table>
            <thead><tr><th>Araç</th><th>Kilometre</th><th>Bakım</th><th>Hatırlatıcı</th><th>Durum</th></tr></thead>
            <tbody>{vehicles.map((vehicle) => <tr key={vehicle.id}><td><strong>{vehicle.nickname}</strong><br /><small>{vehicle.vehicleName}<br />{vehicle.makeName} {vehicle.modelName} · {vehicle.generationName} · {vehicle.engineName}</small></td><td>{vehicle.currentOdometerKm?.toLocaleString('tr-TR') || '—'} km</td><td>{vehicle.maintenanceRecordCount} kayıt<br /><small>Son: {formatDate(vehicle.lastServiceDateUtc)}</small></td><td>{vehicle.openReminderCount} açık<br /><small>{vehicle.dueReminderCount} vadesi gelmiş</small></td><td>{vehicle.isActive ? 'Aktif' : 'Arşivlenmiş'}</td></tr>)}</tbody>
          </table>
        </div>
      )}
    </div>
  );
};

export default GarageAdminPanel;
