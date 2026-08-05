import { useCallback, useEffect, useState } from 'react';
import { adminAPI } from '../services/api';

const displayDate = (value) => value
  ? new Intl.DateTimeFormat('tr-TR', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value))
  : '—';
const displayMoney = (value) => new Intl.NumberFormat('tr-TR', {
  style: 'currency',
  currency: 'TRY',
}).format(value);

const ChannelAdminPanel = ({ role }) => {
  const canManage = ['admin', 'superadmin'].includes(role?.toLowerCase());
  const [channels, setChannels] = useState([]);
  const [listingForm, setListingForm] = useState({ channelId: '', productId: '', externalListingId: '' });
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState(null);

  const loadChannels = useCallback(async () => {
    setBusy('load');
    try {
      const response = await adminAPI.getSalesChannels();
      setChannels(response.data);
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Satış kanalları yüklenemedi.' });
    } finally {
      setBusy('');
    }
  }, []);

  useEffect(() => {
    loadChannels();
  }, [loadChannels]);

  const mutate = async (key, action, successText) => {
    setBusy(key);
    setMessage(null);
    try {
      await action();
      await loadChannels();
      setMessage({ type: 'success', text: successText });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Kanal işlemi tamamlanamadı.' });
    } finally {
      setBusy('');
    }
  };

  const updateState = (channel, requestedEnabled, mode) => mutate(
    `state-${channel.id}`,
    () => adminAPI.updateSalesChannelState(channel.id, {
      requestedEnabled,
      mode,
      concurrencyToken: channel.concurrencyToken,
    }),
    requestedEnabled ? 'Kanal etkinleştirme isteği uygulandı.' : 'Kanal güvenli biçimde kapatıldı.'
  );

  const refreshListing = (channelId, productId, externalListingId) => mutate(
    `listing-${channelId}-${productId}`,
    () => adminAPI.refreshChannelListing(channelId, productId, { externalListingId: externalListingId || null }),
    'Ürün/stok/fiyat senkronizasyon isteği kaydedildi.'
  );

  const submitListing = async (event) => {
    event.preventDefault();
    await refreshListing(
      Number(listingForm.channelId),
      Number(listingForm.productId),
      listingForm.externalListingId
    );
  };

  return (
    <section className="admin-catalog-form">
      <div className="management-header">
        <div>
          <h2>Pazaryeri kanalları</h2>
          <p>Credential değerleri gösterilmez. İstenen durum ile adapter’ın gerçek hazır olma durumu ayrı tutulur.</p>
        </div>
        <button type="button" className="add-button" onClick={loadChannels} disabled={Boolean(busy)}>Kanalları yenile</button>
      </div>
      {message && <div className={`admin-operation-message ${message.type}`}>{message.text}</div>}

      <div className="operation-card-grid">
        {channels.map((channel) => (
          <article className="operation-card" key={channel.id}>
            <strong>{channel.displayName}</strong>
            <span>İstenen durum: {channel.requestedEnabled ? `${channel.mode} / açık` : 'kapalı'}</span>
            <span>Adapter: {channel.adapter.isConfigured ? 'configured' : channel.adapter.statusCode}</span>
            <span>Efektif durum: {channel.adapter.effectiveEnabled ? 'çalışıyor' : 'fail-closed'}</span>
            <span>{channel.listings.length} listing · {channel.inbox.length} inbox olayı</span>
            {canManage && (
              <div className="operation-actions">
                {channel.requestedEnabled ? (
                  <button type="button" className="delete-btn" onClick={() => updateState(channel, false, 'Disabled')} disabled={Boolean(busy)}>Kapat</button>
                ) : (
                  <>
                    <button type="button" className="edit-btn" onClick={() => updateState(channel, true, 'Sandbox')} disabled={Boolean(busy) || !channel.adapter.supportsSandbox}>Sandbox aç</button>
                    <button type="button" className="add-button" onClick={() => updateState(channel, true, 'Production')} disabled={Boolean(busy) || !channel.adapter.supportsProduction}>Production aç</button>
                  </>
                )}
              </div>
            )}
          </article>
        ))}
      </div>

      {canManage && (
        <form className="admin-catalog-form" onSubmit={submitListing}>
          <h3>Ürün listing eşlemesi / senkron isteği</h3>
          <div className="catalog-form-grid">
            <label>Kanal<select required value={listingForm.channelId} onChange={(event) => setListingForm({ ...listingForm, channelId: event.target.value })}><option value="">Seçin</option>{channels.map((channel) => <option key={channel.id} value={channel.id}>{channel.displayName}</option>)}</select></label>
            <label>Ürün ID<input required type="number" min="1" value={listingForm.productId} onChange={(event) => setListingForm({ ...listingForm, productId: event.target.value })} /></label>
            <label>Dış listing ID<input maxLength="100" value={listingForm.externalListingId} onChange={(event) => setListingForm({ ...listingForm, externalListingId: event.target.value })} /></label>
          </div>
          <button type="submit" className="add-button" disabled={Boolean(busy)}>Senkron iste</button>
        </form>
      )}

      {channels.map((channel) => (
        <div key={`listing-${channel.id}`} className="admin-catalog-form">
          <h3>{channel.displayName}: listing ve mutabakat</h3>
          <div className="orders-table operations-table payments-table">
            <table>
              <thead><tr><th>Ürün</th><th>İstenen</th><th>Gözlenen</th><th>Durum</th><th>İşlem</th></tr></thead>
              <tbody>
                {channel.listings.map((listing) => (
                  <tr key={listing.id}>
                    <td>#{listing.productId} {listing.product}<br /><small>{listing.externalListingId || 'Dış listing bekleniyor'}</small></td>
                    <td>{displayMoney(listing.desiredPrice)} · {listing.desiredStock} adet<br /><small>{displayDate(listing.desiredAtUtc)}</small></td>
                    <td>{listing.observedPrice == null ? 'Henüz gözlenmedi' : `${displayMoney(listing.observedPrice)} · ${listing.observedStock} adet`}<br /><small>{displayDate(listing.lastSuccessAtUtc)}</small></td>
                    <td>{listing.status}{listing.hasDrift ? ' / SAPMA' : ''}<br /><small>{listing.lastFailureCode || '—'}</small></td>
                    <td>{canManage && <button type="button" className="edit-btn" onClick={() => refreshListing(channel.id, listing.productId, listing.externalListingId)} disabled={Boolean(busy)}>Yeniden dene</button>}</td>
                  </tr>
                ))}
                {channel.listings.length === 0 && <tr><td colSpan="5">Listing eşlemesi yok.</td></tr>}
              </tbody>
            </table>
          </div>
          <h3>Son sipariş inbox olayları</h3>
          <div className="orders-table operations-table">
            <table>
              <thead><tr><th>Olay</th><th>Durum</th><th>Yerel sipariş</th><th>Zaman</th></tr></thead>
              <tbody>
                {channel.inbox.map((inbox) => <tr key={inbox.id}><td>#{inbox.id}</td><td>{inbox.status}<br /><small>{inbox.failureCode || '—'}</small></td><td>{inbox.orderNumber || '—'}</td><td>{displayDate(inbox.receivedAtUtc)}</td></tr>)}
                {channel.inbox.length === 0 && <tr><td colSpan="4">Inbox olayı yok.</td></tr>}
              </tbody>
            </table>
          </div>
        </div>
      ))}
    </section>
  );
};

export default ChannelAdminPanel;
