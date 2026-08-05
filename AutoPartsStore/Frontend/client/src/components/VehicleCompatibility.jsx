import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router';
import { fitmentAPI } from '../services/api';
import './VehicleCompatibility.css';

const GARAGE_KEY = 'parca-muhendisi:selected-vehicle';

const emptyOptions = {
  makes: [],
  models: [],
  generations: [],
  engines: [],
  vehicles: [],
};

const MATCH_LABELS = {
  Exact: 'Doğrudan uyumlu',
  Compatible: 'Uyumlu',
  Unknown: 'Uyumluluk doğrulanamadı',
};

const CONFIDENCE_LABELS = {
  VeryHigh: 'Çok yüksek güven',
  High: 'Yüksek güven',
  Medium: 'Orta güven',
  Low: 'Düşük güven',
  Unknown: 'Güven puanı yok',
};

const confidenceText = (confidence, band) => {
  if (confidence == null) return CONFIDENCE_LABELS[band] || CONFIDENCE_LABELS.Unknown;
  return `${CONFIDENCE_LABELS[band] || band} · %${Math.round(Number(confidence) * 100)}`;
};

const VehicleCompatibility = ({ productId }) => {
  const [options, setOptions] = useState(emptyOptions);
  const [selection, setSelection] = useState({
    makeId: '',
    modelId: '',
    generationId: '',
    engineId: '',
    vehicleId: '',
  });
  const [knownFitments, setKnownFitments] = useState([]);
  const [result, setResult] = useState(null);
  const [savedVehicle, setSavedVehicle] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    Promise.all([
      fitmentAPI.getMakes(),
      fitmentAPI.getForProduct(productId, { limit: 50 }),
    ])
      .then(([makesResponse, fitmentsResponse]) => {
        if (cancelled) return;
        setOptions((current) => ({ ...current, makes: makesResponse.data }));
        setKnownFitments(fitmentsResponse.data.items || []);
      })
      .catch(() => {
        if (!cancelled) setError('Araç uyumluluk verisi şu anda yüklenemedi.');
      });
    return () => { cancelled = true; };
  }, [productId]);

  useEffect(() => {
    let cancelled = false;
    try {
      const parsed = JSON.parse(window.localStorage.getItem(GARAGE_KEY));
      if (!Number.isInteger(parsed?.vehicleId) || parsed.vehicleId <= 0) return undefined;
      setSavedVehicle(parsed);
      fitmentAPI.check(productId, parsed.vehicleId)
        .then((response) => {
          if (!cancelled) setResult(response.data);
        })
        .catch(() => {
          if (!cancelled) setError('Kayıtlı aracınız için uyumluluk sonucu alınamadı.');
        });
    } catch {
      window.localStorage.removeItem(GARAGE_KEY);
    }
    return () => { cancelled = true; };
  }, [productId]);

  const updateSelection = useCallback(async (field, value) => {
    setResult(null);
    setError('');
    const next = { ...selection, [field]: value };

    if (field === 'makeId') {
      Object.assign(next, { modelId: '', generationId: '', engineId: '', vehicleId: '' });
      setOptions((current) => ({ ...current, models: [], generations: [], engines: [], vehicles: [] }));
    } else if (field === 'modelId') {
      Object.assign(next, { generationId: '', engineId: '', vehicleId: '' });
      setOptions((current) => ({ ...current, generations: [], engines: [], vehicles: [] }));
    } else if (field === 'generationId') {
      Object.assign(next, { engineId: '', vehicleId: '' });
      setOptions((current) => ({ ...current, engines: [], vehicles: [] }));
    } else if (field === 'engineId') {
      next.vehicleId = '';
      setOptions((current) => ({ ...current, vehicles: [] }));
    }

    setSelection(next);
    if (!value) return;

    try {
      let response;
      if (field === 'makeId') response = await fitmentAPI.getModels(value);
      if (field === 'modelId') response = await fitmentAPI.getGenerations(value);
      if (field === 'generationId') response = await fitmentAPI.getEngines(value);
      if (field === 'engineId') response = await fitmentAPI.getConfigurations(value);
      if (response) {
        const target = {
          makeId: 'models',
          modelId: 'generations',
          generationId: 'engines',
          engineId: 'vehicles',
        }[field];
        setOptions((current) => ({ ...current, [target]: response.data }));
      }
    } catch {
      setError('Araç seçenekleri yüklenemedi. Lütfen tekrar deneyin.');
    }
  }, [selection]);

  const selectedVehicle = useMemo(
    () => options.vehicles.find((vehicle) => String(vehicle.id) === String(selection.vehicleId)),
    [options.vehicles, selection.vehicleId]
  );

  const checkCompatibility = async () => {
    if (!selection.vehicleId) return;
    setLoading(true);
    setError('');
    try {
      const response = await fitmentAPI.check(productId, selection.vehicleId);
      setResult(response.data);
      window.localStorage.setItem(GARAGE_KEY, JSON.stringify({
        vehicleId: Number(selection.vehicleId),
        name: selectedVehicle?.name || '',
      }));
    } catch {
      setError('Uyumluluk kontrolü tamamlanamadı.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <section className="fitment-checker" aria-labelledby="fitment-title">
      <div className="fitment-heading">
        <div>
          <h3 id="fitment-title">Aracına uygun mu?</h3>
          <p>Motor ve araç konfigürasyonunu seçerek doğrulanmış katalog kaydını kontrol et.</p>
        </div>
        <span className="verified-data-label">Doğrulanmış veri</span>
      </div>

      {savedVehicle && (
        <div className="saved-fitment-vehicle">
          <span>Garajdaki araç: <strong>{savedVehicle.name || `Araç #${savedVehicle.vehicleId}`}</strong></span>
          <Link to="/garajim">Garajı yönet</Link>
        </div>
      )}

      <div className="fitment-select-grid">
        <select aria-label="Marka" value={selection.makeId} onChange={(event) => updateSelection('makeId', event.target.value)}>
          <option value="">Marka seç</option>
          {options.makes.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
        </select>
        <select aria-label="Model" value={selection.modelId} disabled={!selection.makeId} onChange={(event) => updateSelection('modelId', event.target.value)}>
          <option value="">Model seç</option>
          {options.models.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
        </select>
        <select aria-label="Nesil" value={selection.generationId} disabled={!selection.modelId} onChange={(event) => updateSelection('generationId', event.target.value)}>
          <option value="">Nesil / yıl seç</option>
          {options.generations.map((item) => <option key={item.id} value={item.id}>{item.name} {item.startYear ? `(${item.startYear}-${item.endYear || ''})` : ''}</option>)}
        </select>
        <select aria-label="Motor" value={selection.engineId} disabled={!selection.generationId} onChange={(event) => updateSelection('engineId', event.target.value)}>
          <option value="">Motor seç</option>
          {options.engines.map((item) => <option key={item.id} value={item.id}>{item.name}{item.engineCode ? ` · ${item.engineCode}` : ''}</option>)}
        </select>
        <select aria-label="Araç konfigürasyonu" value={selection.vehicleId} disabled={!selection.engineId} onChange={(event) => updateSelection('vehicleId', event.target.value)}>
          <option value="">Konfigürasyon seç</option>
          {options.vehicles.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
        </select>
        <button type="button" onClick={checkCompatibility} disabled={!selection.vehicleId || loading}>
          {loading ? 'Kontrol ediliyor…' : 'Uyumluluğu kontrol et'}
        </button>
      </div>

      {error && <p className="fitment-message error" role="alert">{error}</p>}
      {result && (
        <div className={`fitment-result ${result.match.toLowerCase()}`} role="status">
          <strong>{MATCH_LABELS[result.match] || result.match}</strong>
          <span>{result.message || (result.isVerified ? 'Geçerli ve doğrulanmış katalog eşleşmesi bulundu.' : 'Satın almadan önce uzman desteği alın.')}</span>
          <small>{confidenceText(result.confidence, result.confidenceBand)}{result.sourceName ? ` · Kaynak: ${result.sourceName}` : ''}</small>
        </div>
      )}

      {knownFitments.length > 0 && (
        <details className="known-fitments">
          <summary>{knownFitments.length} doğrulanmış araç kaydını göster</summary>
          <ul>
            {knownFitments.map((item) => (
              <li key={item.vehicleId}>
                <strong>{item.makeName} {item.modelName}</strong> — {item.generationName}, {item.engineName}
                <small>{confidenceText(item.confidence, item.confidenceBand)} · Kaynak: {item.sourceName}</small>
              </li>
            ))}
          </ul>
        </details>
      )}
      <p className="fitment-disclaimer">Uyumluluk sonucu seçtiğiniz araç bilgisine ve belirtilen tarihte geçerli doğrulanmış kayda dayanır; VIN doğrulaması veya montaj uzmanı kontrolünün yerine geçmez.</p>
    </section>
  );
};

export default VehicleCompatibility;
