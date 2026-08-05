import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router';
import { useAuth } from '../context/AuthContext';
import { fitmentAPI, garageAPI, productsAPI } from '../services/api';
import './GaragePage.css';

const today = () => new Date().toISOString().slice(0, 10);
const SELECTED_VEHICLE_KEY = 'parca-muhendisi:selected-vehicle';
const numberOrNull = (value) => value === '' ? null : Number(value);
const emptyVehicleOptions = { makes: [], models: [], generations: [], engines: [], vehicles: [] };

const GaragePage = () => {
  const { user } = useAuth();
  const [garage, setGarage] = useState([]);
  const [selectedId, setSelectedId] = useState(null);
  const [records, setRecords] = useState([]);
  const [reminders, setReminders] = useState([]);
  const [products, setProducts] = useState([]);
  const [options, setOptions] = useState(emptyVehicleOptions);
  const [selection, setSelection] = useState({ makeId: '', modelId: '', generationId: '', engineId: '', vehicleId: '' });
  const [vehicleForm, setVehicleForm] = useState({ nickname: '', currentOdometerKm: '' });
  const [maintenanceForm, setMaintenanceForm] = useState({
    serviceDateUtc: today(), odometerKm: '', serviceProvider: '', notes: '',
    productId: '', serviceType: 'PeriodicMaintenance', description: '', quantity: 1, unitCost: '',
  });
  const [reminderForm, setReminderForm] = useState({ title: '', dueDateUtc: '', dueOdometerKm: '' });
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState(null);

  const selectedVehicle = useMemo(
    () => garage.find((vehicle) => vehicle.id === selectedId) || null,
    [garage, selectedId]
  );
  const replacementProducts = useMemo(() => {
    const unique = new Map();
    records.forEach((record) => record.items.forEach((item) => {
      if (item.productId && !unique.has(item.productId)) unique.set(item.productId, item);
    }));
    return [...unique.values()];
  }, [records]);

  const loadGarage = useCallback(async () => {
    const response = await garageAPI.getAll();
    setGarage(response.data);
    setSelectedId((current) => current || response.data[0]?.id || null);
  }, []);

  useEffect(() => {
    if (!user) return;
    Promise.all([garageAPI.getAll(), fitmentAPI.getMakes(), productsAPI.getAll({ page: 1, pageSize: 100 })])
      .then(([garageResponse, makesResponse, productsResponse]) => {
        setGarage(garageResponse.data);
        setSelectedId(garageResponse.data[0]?.id || null);
        setOptions((current) => ({ ...current, makes: makesResponse.data }));
        setProducts(productsResponse.data);
      })
      .catch(() => setMessage({ type: 'error', text: 'Garaj bilgileri yüklenemedi.' }));
  }, [user]);

  useEffect(() => {
    if (!selectedId) {
      setRecords([]);
      setReminders([]);
      return;
    }
    Promise.all([
      garageAPI.getMaintenance(selectedId),
      garageAPI.getReminders(selectedId),
    ])
      .then(([recordsResponse, remindersResponse]) => {
        setRecords(recordsResponse.data);
        setReminders(remindersResponse.data);
      })
      .catch(() => setMessage({ type: 'error', text: 'Bakım geçmişi yüklenemedi.' }));
  }, [selectedId]);

  useEffect(() => {
    if (!selectedVehicle) return;
    window.localStorage.setItem(SELECTED_VEHICLE_KEY, JSON.stringify({
      vehicleId: selectedVehicle.vehicleId,
      name: selectedVehicle.vehicleName || selectedVehicle.nickname,
    }));
    setMaintenanceForm((current) => ({
      ...current,
      odometerKm: selectedVehicle.currentOdometerKm ?? '',
    }));
  }, [selectedVehicle]);

  const updateSelection = async (field, value) => {
    const next = { ...selection, [field]: value };
    const target = { makeId: 'models', modelId: 'generations', generationId: 'engines', engineId: 'vehicles' }[field];
    if (field === 'makeId') Object.assign(next, { modelId: '', generationId: '', engineId: '', vehicleId: '' });
    if (field === 'modelId') Object.assign(next, { generationId: '', engineId: '', vehicleId: '' });
    if (field === 'generationId') Object.assign(next, { engineId: '', vehicleId: '' });
    if (field === 'engineId') next.vehicleId = '';
    setSelection(next);
    if (!value || !target) return;
    try {
      const response = field === 'makeId' ? await fitmentAPI.getModels(value)
        : field === 'modelId' ? await fitmentAPI.getGenerations(value)
          : field === 'generationId' ? await fitmentAPI.getEngines(value)
            : await fitmentAPI.getConfigurations(value);
      setOptions((current) => ({ ...current, [target]: response.data }));
    } catch {
      setMessage({ type: 'error', text: 'Araç kataloğu seçeneği yüklenemedi.' });
    }
  };

  const run = async (name, operation, successText) => {
    setBusy(name);
    setMessage(null);
    try {
      await operation();
      setMessage({ type: 'success', text: successText });
      return true;
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'İşlem tamamlanamadı.' });
      return false;
    } finally {
      setBusy('');
    }
  };

  const addVehicle = async (event) => {
    event.preventDefault();
    const ok = await run('vehicle', () => garageAPI.createVehicle({
      vehicleId: Number(selection.vehicleId),
      nickname: vehicleForm.nickname,
      currentOdometerKm: numberOrNull(vehicleForm.currentOdometerKm),
    }, window.crypto.randomUUID()), 'Araç garaja eklendi.');
    if (ok) {
      await loadGarage();
      setVehicleForm({ nickname: '', currentOdometerKm: '' });
    }
  };

  const updateOdometer = async (event) => {
    event.preventDefault();
    const odometer = Number(event.currentTarget.elements.odometer.value);
    const ok = await run('odometer', () => garageAPI.updateVehicle(selectedVehicle.id, {
      nickname: selectedVehicle.nickname,
      currentOdometerKm: odometer,
      isActive: selectedVehicle.isActive,
      concurrencyToken: selectedVehicle.concurrencyToken,
    }), 'Kilometre güncellendi.');
    if (ok) await loadGarage();
  };

  const addMaintenance = async (event) => {
    event.preventDefault();
    const payload = {
      serviceDateUtc: new Date(`${maintenanceForm.serviceDateUtc}T12:00:00Z`).toISOString(),
      odometerKm: Number(maintenanceForm.odometerKm),
      serviceProvider: maintenanceForm.serviceProvider || null,
      notes: maintenanceForm.notes || null,
      items: [{
        productId: numberOrNull(maintenanceForm.productId),
        serviceType: maintenanceForm.serviceType,
        description: maintenanceForm.description,
        quantity: Number(maintenanceForm.quantity),
        unitCost: numberOrNull(maintenanceForm.unitCost),
      }],
    };
    const ok = await run('maintenance', () => garageAPI.addMaintenance(
      selectedId,
      payload,
      window.crypto.randomUUID()
    ), 'Bakım kaydı eklendi.');
    if (ok) {
      const [recordResponse] = await Promise.all([garageAPI.getMaintenance(selectedId), loadGarage()]);
      setRecords(recordResponse.data);
      setMaintenanceForm((current) => ({ ...current, productId: '', description: '', notes: '', unitCost: '' }));
    }
  };

  const addReminder = async (event) => {
    event.preventDefault();
    const ok = await run('reminder', () => garageAPI.addReminder(selectedId, {
      title: reminderForm.title,
      dueDateUtc: reminderForm.dueDateUtc ? new Date(`${reminderForm.dueDateUtc}T12:00:00Z`).toISOString() : null,
      dueOdometerKm: numberOrNull(reminderForm.dueOdometerKm),
    }, window.crypto.randomUUID()), 'Hatırlatıcı eklendi.');
    if (ok) {
      setReminders((await garageAPI.getReminders(selectedId)).data);
      setReminderForm({ title: '', dueDateUtc: '', dueOdometerKm: '' });
    }
  };

  const completeReminder = async (reminder) => {
    const ok = await run(`reminder-${reminder.id}`, () =>
      garageAPI.completeReminder(reminder.id, reminder.concurrencyToken), 'Hatırlatıcı tamamlandı.');
    if (ok) setReminders((await garageAPI.getReminders(selectedId)).data);
  };

  if (!user) {
    return <main className="garage-page"><section className="garage-shell"><h1>Garajım</h1><p>Garaj ve bakım geçmişi için giriş yapmanız gerekir.</p><Link className="garage-primary" to="/login">Giriş yap</Link></section></main>;
  }

  return (
    <main className="garage-page">
      <div className="garage-shell">
        <header className="garage-hero">
          <div><span className="garage-eyebrow">Araç yaşam döngüsü</span><h1>Garajım ve bakım günlüğüm</h1><p>Katalog aracınızı kaydedin, kilometre ve bakım geçmişini takip edin. Plaka veya VIN toplamıyoruz.</p></div>
          <span className="garage-count">{garage.length} araç</span>
        </header>

        {message && <div className={`garage-message ${message.type}`} role={message.type === 'error' ? 'alert' : 'status'}>{message.text}</div>}

        <section className="garage-panel">
          <h2>Aracını ekle</h2>
          <form onSubmit={addVehicle} className="garage-form-grid">
            <label>Marka<select required value={selection.makeId} onChange={(event) => updateSelection('makeId', event.target.value)}><option value="">Seçin</option>{options.makes.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
            <label>Model<select required disabled={!selection.makeId} value={selection.modelId} onChange={(event) => updateSelection('modelId', event.target.value)}><option value="">Seçin</option>{options.models.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
            <label>Nesil<select required disabled={!selection.modelId} value={selection.generationId} onChange={(event) => updateSelection('generationId', event.target.value)}><option value="">Seçin</option>{options.generations.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
            <label>Motor<select required disabled={!selection.generationId} value={selection.engineId} onChange={(event) => updateSelection('engineId', event.target.value)}><option value="">Seçin</option>{options.engines.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
            <label>Konfigürasyon<select required disabled={!selection.engineId} value={selection.vehicleId} onChange={(event) => updateSelection('vehicleId', event.target.value)}><option value="">Seçin</option>{options.vehicles.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
            <label>Araç takma adı<input required maxLength="80" value={vehicleForm.nickname} onChange={(event) => setVehicleForm({ ...vehicleForm, nickname: event.target.value })} placeholder="Örn. Aile aracı" /></label>
            <label>Güncel kilometre<input type="number" min="0" max="10000000" value={vehicleForm.currentOdometerKm} onChange={(event) => setVehicleForm({ ...vehicleForm, currentOdometerKm: event.target.value })} /></label>
            <button className="garage-primary" disabled={busy === 'vehicle'}>{busy === 'vehicle' ? 'Ekleniyor…' : 'Garaja ekle'}</button>
          </form>
        </section>

        {garage.length > 0 && (
          <div className="garage-layout">
            <aside className="garage-list" aria-label="Kayıtlı araçlar">
              {garage.map((vehicle) => (
                <button type="button" key={vehicle.id} className={vehicle.id === selectedId ? 'active' : ''} onClick={() => setSelectedId(vehicle.id)}>
                  <strong>{vehicle.nickname}</strong><span>{vehicle.makeName} {vehicle.modelName}</span><small>{vehicle.currentOdometerKm?.toLocaleString('tr-TR') || '—'} km</small>
                </button>
              ))}
            </aside>

            {selectedVehicle && (
              <div className="garage-detail">
                <section className="garage-panel garage-vehicle-summary">
                  <div><span className="garage-eyebrow">Seçili araç</span><h2>{selectedVehicle.nickname}</h2><p>{selectedVehicle.vehicleName || `${selectedVehicle.makeName || ''} ${selectedVehicle.modelName || ''}`}</p></div>
                  <form onSubmit={updateOdometer}><label>Güncel km<input name="odometer" type="number" min={selectedVehicle.currentOdometerKm ?? 0} max="10000000" required defaultValue={selectedVehicle.currentOdometerKm ?? 0} /></label><button className="garage-secondary" disabled={busy === 'odometer'}>Güncelle</button></form>
                </section>

                <section className="garage-panel">
                  <h2>Bakım kaydı ekle</h2>
                  <form className="garage-form-grid" onSubmit={addMaintenance}>
                    <label>Bakım tarihi<input type="date" required max={today()} value={maintenanceForm.serviceDateUtc} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, serviceDateUtc: event.target.value })} /></label>
                    <label>Kilometre<input type="number" min="0" max="10000000" required value={maintenanceForm.odometerKm} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, odometerKm: event.target.value })} /></label>
                    <label>İşlem türü<select value={maintenanceForm.serviceType} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, serviceType: event.target.value })}><option value="PeriodicMaintenance">Periyodik bakım</option><option value="OilChange">Yağ değişimi</option><option value="FilterChange">Filtre değişimi</option><option value="BrakeService">Fren bakımı</option><option value="Repair">Onarım</option><option value="Inspection">Kontrol</option></select></label>
                    <label>Servis / usta<input maxLength="120" value={maintenanceForm.serviceProvider} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, serviceProvider: event.target.value })} /></label>
                    <label className="garage-span-two">Kullanılan katalog ürünü (isteğe bağlı)<select value={maintenanceForm.productId} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, productId: event.target.value })}><option value="">Katalog ürünü bağlama</option>{products.map((product) => <option key={product.id} value={product.id}>{product.partNumber} · {product.name}</option>)}</select></label>
                    <label className="garage-span-two">İşlem açıklaması<input required maxLength="250" value={maintenanceForm.description} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, description: event.target.value })} /></label>
                    <label>Adet<input type="number" min="1" max="1000" required value={maintenanceForm.quantity} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, quantity: event.target.value })} /></label>
                    <label>Birim maliyet (TL)<input type="number" min="0" step="0.01" value={maintenanceForm.unitCost} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, unitCost: event.target.value })} /></label>
                    <label className="garage-span-two">Not<textarea maxLength="1000" value={maintenanceForm.notes} onChange={(event) => setMaintenanceForm({ ...maintenanceForm, notes: event.target.value })} /></label>
                    <button className="garage-primary" disabled={busy === 'maintenance'}>Bakımı kaydet</button>
                  </form>
                  <div className="garage-timeline">
                    {records.length === 0 ? <p>Henüz bakım kaydı yok.</p> : records.map((record) => <article key={record.id}><time>{new Date(record.serviceDateUtc).toLocaleDateString('tr-TR')}</time><div><strong>{record.odometerKm.toLocaleString('tr-TR')} km</strong>{record.items.map((item) => <p key={item.id}>{item.description} · {item.quantity} adet{item.unitCost != null ? ` · ${item.unitCost.toLocaleString('tr-TR')} TL` : ''}{item.productId && <> · <Link to={`/product/${item.productId}`}>{item.productName || 'Ürünü aç'}</Link></>}</p>)}{record.serviceProvider && <small>{record.serviceProvider}</small>}</div></article>)}
                  </div>
                  {replacementProducts.length > 0 && <div className="garage-repurchase"><h3>Geçmişte kullandığın parçalar</h3><p>Değişim zamanı geldiğinde aynı ürünün güncel stok, fiyat ve araç uyumluluğunu yeniden kontrol et.</p><div>{replacementProducts.map((item) => <Link key={item.productId} to={`/product/${item.productId}`}>{item.productName || `Ürün #${item.productId}`} →</Link>)}</div></div>}
                </section>

                <section className="garage-panel">
                  <h2>Bakım hatırlatıcıları</h2>
                  <p className="garage-note">Şimdilik hesap içi takip yapılır. E-posta/SMS sağlayıcısı yapılandırılmadan dış bildirim gönderilmiş sayılmaz.</p>
                  <form className="garage-form-grid" onSubmit={addReminder}>
                    <label>Başlık<input required maxLength="120" value={reminderForm.title} onChange={(event) => setReminderForm({ ...reminderForm, title: event.target.value })} /></label>
                    <label>Hedef tarih<input type="date" value={reminderForm.dueDateUtc} onChange={(event) => setReminderForm({ ...reminderForm, dueDateUtc: event.target.value })} /></label>
                    <label>Hedef kilometre<input type="number" min="0" max="10000000" value={reminderForm.dueOdometerKm} onChange={(event) => setReminderForm({ ...reminderForm, dueOdometerKm: event.target.value })} /></label>
                    <button className="garage-primary" disabled={busy === 'reminder' || (!reminderForm.dueDateUtc && !reminderForm.dueOdometerKm)}>Hatırlatıcı ekle</button>
                  </form>
                  <div className="garage-reminders">
                    {reminders.map((reminder) => <article key={reminder.id} className={`reminder-${reminder.status.toLowerCase()}`}><div><strong>{reminder.title}</strong><span>{reminder.dueDateUtc ? new Date(reminder.dueDateUtc).toLocaleDateString('tr-TR') : ''}{reminder.dueDateUtc && reminder.dueOdometerKm ? ' · ' : ''}{reminder.dueOdometerKm ? `${reminder.dueOdometerKm.toLocaleString('tr-TR')} km` : ''}</span></div><span className="garage-status">{reminder.status === 'Due' ? 'Vadesi geldi' : reminder.status === 'Completed' ? 'Tamamlandı' : 'Yaklaşan'}</span>{reminder.status !== 'Completed' && <button type="button" className="garage-secondary" onClick={() => completeReminder(reminder)} disabled={busy === `reminder-${reminder.id}`}>Tamamla</button>}</article>)}
                  </div>
                </section>
              </div>
            )}
          </div>
        )}
      </div>
    </main>
  );
};

export default GaragePage;
