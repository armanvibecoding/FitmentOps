import { useCallback, useEffect, useState } from 'react';
import { adminAPI } from '../services/api';

const nowIso = () => new Date().toISOString();

const initialVehicle = {
  makeKey: '', makeName: '', modelKey: '', modelName: '',
  generationKey: '', generationName: '', generationStartYear: '', generationEndYear: '',
  engineKey: '', engineName: '', engineCode: '', fuelType: '', displacementCc: '', powerKw: '',
  vehicleKey: '', vehicleName: '', bodyStyle: '', transmission: '', driveType: '', market: 'TR',
  vehicleStartYear: '', vehicleEndYear: '',
};

const numberOrNull = (value) => value === '' ? null : Number(value);

const FitmentAdminPanel = ({ products }) => {
  const [vehicle, setVehicle] = useState(initialVehicle);
  const [vehicleId, setVehicleId] = useState('');
  const [fitment, setFitment] = useState({
    productId: '', assertionKind: 1, confidence: '0.95', isVerified: true,
    sourceKind: 4, sourceName: 'Uzman incelemesi', sourceRecordId: '', provenance: '',
  });
  const [identifier, setIdentifier] = useState({
    productId: '', kind: 1, schemeAuthority: '', value: '', isVerified: true,
    sourceKind: 4, sourceName: 'Uzman incelemesi', sourceRecordId: '', provenance: '',
  });
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState(null);
  const [quality, setQuality] = useState(null);

  const loadQuality = useCallback(async () => {
    try {
      const response = await adminAPI.getFitmentQuality();
      setQuality(response.data);
    } catch {
      setMessage({ type: 'error', text: 'Uyumluluk kalite özeti yüklenemedi.' });
    }
  }, []);

  useEffect(() => {
    loadQuality();
  }, [loadQuality]);

  const run = async (name, operation) => {
    setBusy(name);
    setMessage(null);
    try {
      const response = await operation();
      setMessage({ type: 'success', text: `${response.data.outcome}: kayıt #${response.data.id}` });
      await loadQuality();
      return response.data;
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'Katalog işlemi tamamlanamadı.' });
      return null;
    } finally {
      setBusy('');
    }
  };

  const submitVehicle = async (event) => {
    event.preventDefault();
    const payload = {
      ...vehicle,
      generationStartYear: numberOrNull(vehicle.generationStartYear),
      generationEndYear: numberOrNull(vehicle.generationEndYear),
      displacementCc: numberOrNull(vehicle.displacementCc),
      powerKw: numberOrNull(vehicle.powerKw),
      vehicleStartYear: numberOrNull(vehicle.vehicleStartYear),
      vehicleEndYear: numberOrNull(vehicle.vehicleEndYear),
    };
    const result = await run('vehicle', () => adminAPI.upsertVehicleTree(payload));
    if (result?.id) setVehicleId(String(result.id));
  };

  const submitFitment = async (event) => {
    event.preventDefault();
    await run('fitment', () => adminAPI.upsertProductFitment({
      ...fitment,
      productId: Number(fitment.productId),
      vehicleId: Number(vehicleId),
      assertionKind: Number(fitment.assertionKind),
      confidence: Number(fitment.confidence),
      sourceKind: Number(fitment.sourceKind),
      idempotencyKey: window.crypto.randomUUID(),
      validFromUtc: nowIso(),
      validToUtc: null,
    }));
  };

  const submitIdentifier = async (event) => {
    event.preventDefault();
    await run('identifier', () => adminAPI.upsertProductIdentifier({
      ...identifier,
      productId: Number(identifier.productId),
      kind: Number(identifier.kind),
      sourceKind: Number(identifier.sourceKind),
      validFromUtc: nowIso(),
      validToUtc: null,
    }));
  };

  const field = (key, label, required = true, type = 'text') => (
    <label>
      <span>{label}</span>
      <input type={type} required={required} value={vehicle[key]} onChange={(event) => setVehicle((current) => ({ ...current, [key]: event.target.value }))} />
    </label>
  );

  const productSelect = (state, setState) => (
    <label>
      <span>Ürün</span>
      <select required value={state.productId} onChange={(event) => setState((current) => ({ ...current, productId: event.target.value }))}>
        <option value="">Ürün seç</option>
        {products.map((product) => <option key={product.id} value={product.id}>{product.partNumber} · {product.name}</option>)}
      </select>
    </label>
  );

  return (
    <div className="fitment-admin">
      <div className="management-header">
        <div><h1>Araç ve uyumluluk kataloğu</h1><p>Yalnız kaynağı kanıtlanmış veriyi doğrulanmış olarak işaretleyin.</p></div>
      </div>
      {message && <div className={`admin-operation-message ${message.type}`} role={message.type === 'error' ? 'alert' : 'status'}>{message.text}</div>}

      {quality && (
        <section className="admin-catalog-form" aria-labelledby="fitment-quality-title">
          <h2 id="fitment-quality-title">Katalog kalite kapısı</h2>
          <div className="operation-card-grid">
            <div className="operation-card"><strong>Doğrulanmış aktif eşleşme</strong><span>{quality.activeVerifiedFitments}</span></div>
            <div className="operation-card"><strong>Doğrulanmamış aktif eşleşme</strong><span>{quality.activeUnverifiedFitments}</span></div>
            <div className="operation-card"><strong>Güven eşiği altında</strong><span>{quality.belowConfidenceThreshold}</span></div>
            <div className="operation-card"><strong>Uyumluluk kaydı eksik ürün</strong><span>{quality.productsWithoutVerifiedFitment} / {quality.totalProducts}</span></div>
            <div className="operation-card"><strong>OEM kodu eksik ürün</strong><span>{quality.productsWithoutVerifiedOem} / {quality.totalProducts}</span></div>
            <div className="operation-card"><strong>30 gün içinde süresi dolacak</strong><span>{quality.expiringWithin30Days}</span></div>
          </div>
          <p className="admin-safety-note">Doğrudan eşleşmelerde en az %90, uyumlu eşleşmelerde en az %80 güven gerekir. Eşiğin altındaki kayıt müşteriye “uyumlu” olarak gösterilmez.</p>
          <p className="admin-safety-note">Aktif doğrulanmış kaynaklar: {quality.sources.length > 0 ? quality.sources.map((source) => `${source.sourceKind}: ${source.count}`).join(' · ') : 'Henüz yok'}</p>
        </section>
      )}

      <form className="admin-catalog-form" onSubmit={submitVehicle}>
        <h2>1. Araç ağacı ekle veya doğrula</h2>
        <div className="catalog-form-grid">
          {field('makeKey', 'Marka anahtarı')}{field('makeName', 'Marka adı')}
          {field('modelKey', 'Model anahtarı')}{field('modelName', 'Model adı')}
          {field('generationKey', 'Nesil anahtarı')}{field('generationName', 'Nesil adı')}
          {field('generationStartYear', 'Nesil başlangıç yılı', false, 'number')}{field('generationEndYear', 'Nesil bitiş yılı', false, 'number')}
          {field('engineKey', 'Motor anahtarı')}{field('engineName', 'Motor adı')}
          {field('engineCode', 'Motor kodu', false)}{field('fuelType', 'Yakıt', false)}
          {field('displacementCc', 'Motor hacmi (cc)', false, 'number')}{field('powerKw', 'Güç (kW)', false, 'number')}
          {field('vehicleKey', 'Konfigürasyon anahtarı')}{field('vehicleName', 'Araç görünen adı')}
          {field('bodyStyle', 'Kasa', false)}{field('transmission', 'Şanzıman', false)}
          {field('driveType', 'Çekiş', false)}{field('market', 'Pazar', false)}
          {field('vehicleStartYear', 'Araç başlangıç yılı', false, 'number')}{field('vehicleEndYear', 'Araç bitiş yılı', false, 'number')}
        </div>
        <button className="add-button" disabled={busy === 'vehicle'}>{busy === 'vehicle' ? 'Kaydediliyor…' : 'Araç ağacını kaydet'}</button>
      </form>

      <div className="catalog-two-column">
        <form className="admin-catalog-form" onSubmit={submitFitment}>
          <h2>2. Ürün–araç uyumu</h2>
          {productSelect(fitment, setFitment)}
          <label><span>Araç kayıt ID</span><input type="number" min="1" required value={vehicleId} onChange={(event) => setVehicleId(event.target.value)} /></label>
          <label><span>Eşleşme</span><select value={fitment.assertionKind} onChange={(event) => setFitment((current) => ({ ...current, assertionKind: event.target.value }))}><option value="1">Doğrudan</option><option value="2">Uyumlu</option></select></label>
          <label><span>Güven (0–1)</span><input type="number" min="0" max="1" step="0.01" required value={fitment.confidence} onChange={(event) => setFitment((current) => ({ ...current, confidence: event.target.value }))} /></label>
          <label><span>Kaynak kayıt ID</span><input required value={fitment.sourceRecordId} onChange={(event) => setFitment((current) => ({ ...current, sourceRecordId: event.target.value }))} /></label>
          <label><span>Kanıt / referans</span><textarea required value={fitment.provenance} onChange={(event) => setFitment((current) => ({ ...current, provenance: event.target.value }))} /></label>
          <label className="catalog-check"><input type="checkbox" checked={fitment.isVerified} onChange={(event) => setFitment((current) => ({ ...current, isVerified: event.target.checked }))} /> Uzman tarafından doğrulandı</label>
          <button className="add-button" disabled={busy === 'fitment'}>Uyumu kaydet</button>
        </form>

        <form className="admin-catalog-form" onSubmit={submitIdentifier}>
          <h2>3. OEM / çapraz ürün kodu</h2>
          {productSelect(identifier, setIdentifier)}
          <label><span>Kod türü</span><select value={identifier.kind} onChange={(event) => setIdentifier((current) => ({ ...current, kind: event.target.value }))}><option value="1">OEM</option><option value="2">Interchange</option><option value="3">Üretici parça no</option><option value="4">Tedarikçi SKU</option></select></label>
          <label><span>Kod otoritesi / marka</span><input required value={identifier.schemeAuthority} onChange={(event) => setIdentifier((current) => ({ ...current, schemeAuthority: event.target.value }))} /></label>
          <label><span>Parça kodu</span><input required value={identifier.value} onChange={(event) => setIdentifier((current) => ({ ...current, value: event.target.value }))} /></label>
          <label><span>Kaynak kayıt ID</span><input required value={identifier.sourceRecordId} onChange={(event) => setIdentifier((current) => ({ ...current, sourceRecordId: event.target.value }))} /></label>
          <label><span>Kanıt / referans</span><textarea required value={identifier.provenance} onChange={(event) => setIdentifier((current) => ({ ...current, provenance: event.target.value }))} /></label>
          <label className="catalog-check"><input type="checkbox" checked={identifier.isVerified} onChange={(event) => setIdentifier((current) => ({ ...current, isVerified: event.target.checked }))} /> Uzman tarafından doğrulandı</label>
          <button className="add-button" disabled={busy === 'identifier'}>Ürün kodunu kaydet</button>
        </form>
      </div>
      <p className="admin-safety-note">Kaynak adı/türü backend tarafından sınırlandırılır. Doğrulanmamış ithalat müşteri ekranında hiçbir zaman “uyumlu” sonucuna dönüşmez.</p>
    </div>
  );
};

export default FitmentAdminPanel;
