import { useCallback, useEffect, useMemo, useState } from 'react';
import { adminAPI } from '../services/api';

const dateInputValue = (date = new Date()) => date.toISOString().slice(0, 16);
const futureDateInputValue = (days) => dateInputValue(new Date(Date.now() + days * 86400000));
const toUtc = (value) => new Date(value).toISOString();
const displayDate = (value) => value
  ? new Intl.DateTimeFormat('tr-TR', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(value))
  : '—';
const displayMoney = (value, currency = 'TRY') => new Intl.NumberFormat('tr-TR', {
  style: 'currency',
  currency,
}).format(value);

const initialGroup = { code: '', name: '', priority: 0, isActive: true };
const initialList = {
  code: '',
  name: '',
  customerGroupId: '',
  validFromUtc: dateInputValue(),
  validToUtc: '',
  isActive: true,
};
const initialRule = {
  priceListId: '',
  productId: '',
  brandId: '',
  categoryId: '',
  minimumQuantity: 1,
  minimumPeriodRevenue: 0,
  priority: 0,
  discountPercentage: '',
  fixedUnitPrice: '',
  validFromUtc: dateInputValue(),
  validToUtc: '',
  isActive: true,
};
const initialSupplier = {
  code: '',
  name: '',
  healthStatus: 'Healthy',
  priority: 0,
  isActive: true,
};
const initialOffer = {
  supplierId: '',
  externalOfferId: '',
  productId: '',
  oemNumber: '',
  unitCost: '',
  shippingCost: 0,
  availableQuantity: '',
  leadTimeDays: 0,
  minimumOrderQuantity: 1,
  validUntilUtc: futureDateInputValue(30),
  canDropship: true,
  canSupplyWarehouse: true,
};
const initialSourcing = {
  productId: '',
  quantity: 1,
  oemNumber: '',
  allowSplit: false,
  requireDropship: false,
};

const B2bAdminPanel = ({ role }) => {
  const normalizedRole = role?.toLowerCase();
  const hasAllAccess = ['admin', 'superadmin'].includes(normalizedRole);
  const canReview = hasAllAccess;
  const canPrice = hasAllAccess || normalizedRole === 'finance';
  const canQuote = hasAllAccess || normalizedRole === 'support';
  const canSupply = hasAllAccess || normalizedRole === 'warehouse';
  const [applications, setApplications] = useState([]);
  const [pricing, setPricing] = useState({ groups: [], lists: [], rules: [] });
  const [quotes, setQuotes] = useState([]);
  const [suppliers, setSuppliers] = useState([]);
  const [groupForm, setGroupForm] = useState(initialGroup);
  const [listForm, setListForm] = useState(initialList);
  const [ruleForm, setRuleForm] = useState(initialRule);
  const [supplierForm, setSupplierForm] = useState(initialSupplier);
  const [offerForm, setOfferForm] = useState(initialOffer);
  const [sourcingForm, setSourcingForm] = useState(initialSourcing);
  const [selectedGroups, setSelectedGroups] = useState({});
  const [sourcingResult, setSourcingResult] = useState(null);
  const [busy, setBusy] = useState('');
  const [message, setMessage] = useState(null);

  const activeGroups = useMemo(
    () => pricing.groups.filter((group) => group.isActive),
    [pricing.groups]
  );

  const loadData = useCallback(async () => {
    setBusy('refresh');
    setMessage(null);
    try {
      const requests = [];
      if (canReview) requests.push(['applications', adminAPI.getDealerApplications()]);
      if (canReview || canPrice) requests.push(['pricing', adminAPI.getB2bPricing()]);
      if (canQuote) requests.push(['quotes', adminAPI.getBulkQuotes()]);
      if (canSupply) requests.push(['suppliers', adminAPI.getSuppliers()]);
      const responses = await Promise.all(requests.map(async ([key, request]) => [key, await request]));
      responses.forEach(([key, response]) => {
        if (key === 'applications') setApplications(response.data);
        if (key === 'pricing') setPricing(response.data);
        if (key === 'quotes') setQuotes(response.data);
        if (key === 'suppliers') setSuppliers(response.data);
      });
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'B2B verileri yüklenemedi.' });
    } finally {
      setBusy('');
    }
  }, [canPrice, canQuote, canReview, canSupply]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const mutate = async (key, action, successText) => {
    setBusy(key);
    setMessage(null);
    try {
      const result = await action();
      await loadData();
      setMessage({ type: 'success', text: successText });
      return result;
    } catch (error) {
      setMessage({ type: 'error', text: error.response?.data?.message || 'İşlem tamamlanamadı.' });
      return null;
    } finally {
      setBusy('');
    }
  };

  const reviewApplication = async (application, decision) => {
    const groupId = selectedGroups[application.id] || application.customerGroupId;
    if (['Approve', 'Reactivate'].includes(decision) && !groupId) {
      setMessage({ type: 'error', text: 'Onay veya yeniden etkinleştirme için aktif müşteri grubu seçin.' });
      return;
    }
    await mutate(
      `application-${application.id}`,
      () => adminAPI.reviewDealerApplication(application.id, decision, groupId ? Number(groupId) : null),
      'Bayi başvurusu güncellendi.'
    );
  };

  const createGroup = async (event) => {
    event.preventDefault();
    const result = await mutate(
      'create-group',
      () => adminAPI.createCustomerGroup({ ...groupForm, priority: Number(groupForm.priority) }),
      'Müşteri grubu oluşturuldu.'
    );
    if (result) setGroupForm(initialGroup);
  };

  const toggleGroup = (group) => mutate(
    `group-${group.id}`,
    () => adminAPI.updateCustomerGroup(group.id, {
      name: group.name,
      priority: group.priority,
      isActive: !group.isActive,
      concurrencyToken: group.concurrencyToken,
    }),
    'Müşteri grubu durumu güncellendi.'
  );

  const createList = async (event) => {
    event.preventDefault();
    const result = await mutate(
      'create-list',
      () => adminAPI.createPriceList({
        ...listForm,
        customerGroupId: Number(listForm.customerGroupId),
        validFromUtc: toUtc(listForm.validFromUtc),
        validToUtc: listForm.validToUtc ? toUtc(listForm.validToUtc) : null,
      }),
      'Fiyat listesi oluşturuldu.'
    );
    if (result) setListForm(initialList);
  };

  const toggleList = (list) => mutate(
    `list-${list.id}`,
    () => adminAPI.updatePriceList(list.id, {
      name: list.name,
      customerGroupId: list.customerGroupId,
      validFromUtc: list.validFromUtc,
      validToUtc: list.validToUtc,
      isActive: !list.isActive,
      concurrencyToken: list.concurrencyToken,
    }),
    'Fiyat listesi durumu güncellendi.'
  );

  const mapRule = (form) => ({
    priceListId: Number(form.priceListId),
    productId: form.productId ? Number(form.productId) : null,
    brandId: form.brandId ? Number(form.brandId) : null,
    categoryId: form.categoryId ? Number(form.categoryId) : null,
    minimumQuantity: Number(form.minimumQuantity),
    minimumPeriodRevenue: Number(form.minimumPeriodRevenue),
    priority: Number(form.priority),
    discountPercentage: form.discountPercentage === '' ? null : Number(form.discountPercentage),
    fixedUnitPrice: form.fixedUnitPrice === '' ? null : Number(form.fixedUnitPrice),
    validFromUtc: toUtc(form.validFromUtc),
    validToUtc: form.validToUtc ? toUtc(form.validToUtc) : null,
    isActive: form.isActive,
  });

  const createRule = async (event) => {
    event.preventDefault();
    const result = await mutate(
      'create-rule',
      () => adminAPI.createPriceRule(mapRule(ruleForm)),
      'Fiyat kuralı oluşturuldu.'
    );
    if (result) setRuleForm(initialRule);
  };

  const toggleRule = (rule) => mutate(
    `rule-${rule.id}`,
    () => adminAPI.updatePriceRule(rule.id, {
      priceListId: rule.priceListId,
      productId: rule.productId,
      brandId: rule.brandId,
      categoryId: rule.categoryId,
      minimumQuantity: rule.minimumQuantity,
      minimumPeriodRevenue: rule.minimumPeriodRevenue,
      priority: rule.priority,
      discountPercentage: rule.discountPercentage,
      fixedUnitPrice: rule.fixedUnitPrice,
      validFromUtc: rule.validFromUtc,
      validToUtc: rule.validToUtc,
      isActive: !rule.isActive,
      concurrencyToken: rule.concurrencyToken,
    }),
    'Fiyat kuralı durumu güncellendi.'
  );

  const prepareQuote = async (quote) => {
    const lines = [];
    for (const line of quote.lines) {
      const unitPrice = window.prompt(
        `${line.requestedIdentifier} / ${line.requestedQuantity} adet için birim fiyat. Karşılanamıyorsa boş bırakın:`,
        line.quotedUnitPrice ?? ''
      );
      if (unitPrice === null) return;
      if (unitPrice === '') {
        lines.push({ lineId: line.id, unitPrice: null, availableQuantity: 0, leadTimeDays: 0 });
        continue;
      }
      const availableQuantity = window.prompt('Karşılanabilir adet:', line.availableQuantity ?? line.requestedQuantity);
      if (availableQuantity === null) return;
      const leadTimeDays = window.prompt('Termin süresi (gün):', line.leadTimeDays ?? 0);
      if (leadTimeDays === null) return;
      lines.push({
        lineId: line.id,
        unitPrice: Number(unitPrice),
        availableQuantity: Number(availableQuantity),
        leadTimeDays: Number(leadTimeDays),
      });
    }
    const validUntil = window.prompt('Teklif geçerlilik sonu (YYYY-MM-DD):', futureDateInputValue(7).slice(0, 10));
    if (!validUntil) return;
    await mutate(
      `quote-${quote.id}`,
      () => adminAPI.prepareBulkQuote(quote.id, {
        lines,
        validUntilUtc: new Date(`${validUntil}T23:59:59`).toISOString(),
      }),
      'Toplu teklif hazırlandı.'
    );
  };

  const createSupplier = async (event) => {
    event.preventDefault();
    const result = await mutate(
      'create-supplier',
      () => adminAPI.createSupplier({ ...supplierForm, priority: Number(supplierForm.priority) }),
      'Tedarikçi oluşturuldu.'
    );
    if (result) setSupplierForm(initialSupplier);
  };

  const changeSupplier = (supplier, changes) => mutate(
    `supplier-${supplier.id}`,
    () => adminAPI.updateSupplier(supplier.id, {
      name: supplier.name,
      healthStatus: supplier.healthStatus,
      priority: supplier.priority,
      isActive: supplier.isActive,
      concurrencyToken: supplier.concurrencyToken,
      ...changes,
    }),
    'Tedarikçi güncellendi.'
  );

  const registerOffer = async (event) => {
    event.preventDefault();
    const result = await mutate(
      'create-offer',
      () => adminAPI.registerSupplierOffer({
        ...offerForm,
        supplierId: Number(offerForm.supplierId),
        productId: Number(offerForm.productId),
        unitCost: Number(offerForm.unitCost),
        shippingCost: Number(offerForm.shippingCost),
        availableQuantity: Number(offerForm.availableQuantity),
        leadTimeDays: Number(offerForm.leadTimeDays),
        minimumOrderQuantity: Number(offerForm.minimumOrderQuantity),
        validUntilUtc: toUtc(offerForm.validUntilUtc),
      }),
      'Tedarikçi teklifi kaydedildi.'
    );
    if (result) setOfferForm(initialOffer);
  };

  const toggleOffer = (offer) => mutate(
    `offer-${offer.id}`,
    () => adminAPI.setSupplierOfferActive(offer.id, {
      isActive: !offer.isActive,
      concurrencyToken: offer.concurrencyToken,
    }),
    'Tedarikçi teklifi durumu güncellendi.'
  );

  const selectSource = async (event) => {
    event.preventDefault();
    const result = await mutate(
      'source-select',
      () => adminAPI.selectSupplierSource({
        ...sourcingForm,
        productId: Number(sourcingForm.productId),
        quantity: Number(sourcingForm.quantity),
        oemNumber: sourcingForm.oemNumber || null,
      }),
      'Kaynak seçimi hesaplandı.'
    );
    if (result) setSourcingResult(result.data);
  };

  return (
    <div className="b2b-management">
      <div className="management-header">
        <div>
          <h1>B2B ve tedarik yönetimi</h1>
          <p>Bayi onayı, kurumsal fiyat, RFQ ve tedarikçi kaynak kararları rol bazlı yönetilir.</p>
        </div>
        <button type="button" className="add-button" onClick={loadData} disabled={Boolean(busy)}>
          Yenile
        </button>
      </div>

      {message && <div className={`admin-operation-message ${message.type}`}>{message.text}</div>}

      {canReview && (
        <section className="admin-catalog-form">
          <h2>Bayi başvuruları</h2>
          <div className="orders-table operations-table">
            <table>
              <thead><tr><th>Firma</th><th>İletişim</th><th>Vergi no</th><th>Durum</th><th>Grup / işlem</th></tr></thead>
              <tbody>
                {applications.map((application) => (
                  <tr key={application.id}>
                    <td>{application.companyName}<small><br />Kullanıcı #{application.userId}</small></td>
                    <td>{application.contactName}<br /><small>{application.contactEmail}<br />{application.contactPhone}</small></td>
                    <td>•••• {application.taxNumber?.slice(-4)}</td>
                    <td><span className="status-badge">{application.status}</span><br /><small>{displayDate(application.createdAtUtc)}</small></td>
                    <td>
                      <select
                        value={selectedGroups[application.id] || application.customerGroupId || ''}
                        onChange={(event) => setSelectedGroups((current) => ({ ...current, [application.id]: event.target.value }))}
                        disabled={Boolean(busy)}
                      >
                        <option value="">Müşteri grubu</option>
                        {activeGroups.map((group) => <option key={group.id} value={group.id}>{group.code} — {group.name}</option>)}
                      </select>
                      <div className="operation-actions">
                        {application.status === 'Pending' && <button type="button" className="edit-btn" onClick={() => reviewApplication(application, 'Approve')} disabled={Boolean(busy)}>Onayla</button>}
                        {application.status === 'Pending' && <button type="button" className="delete-btn" onClick={() => reviewApplication(application, 'Reject')} disabled={Boolean(busy)}>Reddet</button>}
                        {application.status === 'Approved' && <button type="button" className="delete-btn" onClick={() => reviewApplication(application, 'Suspend')} disabled={Boolean(busy)}>Askıya al</button>}
                        {application.status === 'Suspended' && <button type="button" className="edit-btn" onClick={() => reviewApplication(application, 'Reactivate')} disabled={Boolean(busy)}>Etkinleştir</button>}
                      </div>
                    </td>
                  </tr>
                ))}
                {applications.length === 0 && <tr><td colSpan="5">Başvuru yok.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {canPrice && (
        <section>
          <h2>Kurumsal fiyatlama</h2>
          <form className="admin-catalog-form" onSubmit={createGroup}>
            <h3>Müşteri grubu ekle</h3>
            <div className="catalog-form-grid">
              <label>Kod<input required maxLength="50" value={groupForm.code} onChange={(event) => setGroupForm({ ...groupForm, code: event.target.value })} /></label>
              <label>Ad<input required maxLength="120" value={groupForm.name} onChange={(event) => setGroupForm({ ...groupForm, name: event.target.value })} /></label>
              <label>Öncelik<input type="number" min="0" value={groupForm.priority} onChange={(event) => setGroupForm({ ...groupForm, priority: event.target.value })} /></label>
              <label className="catalog-check"><input type="checkbox" checked={groupForm.isActive} onChange={(event) => setGroupForm({ ...groupForm, isActive: event.target.checked })} /> Aktif</label>
            </div>
            <button type="submit" className="add-button" disabled={Boolean(busy)}>Grup oluştur</button>
          </form>
          <div className="orders-table operations-table">
            <table><thead><tr><th>Kod</th><th>Ad</th><th>Öncelik</th><th>Durum</th><th>İşlem</th></tr></thead>
              <tbody>{pricing.groups.map((group) => <tr key={group.id}><td>{group.code}</td><td>{group.name}</td><td>{group.priority}</td><td>{group.isActive ? 'Aktif' : 'Pasif'}</td><td><button type="button" className={group.isActive ? 'delete-btn' : 'edit-btn'} onClick={() => toggleGroup(group)} disabled={Boolean(busy)}>{group.isActive ? 'Pasifleştir' : 'Etkinleştir'}</button></td></tr>)}</tbody>
            </table>
          </div>

          <form className="admin-catalog-form" onSubmit={createList}>
            <h3>Fiyat listesi ekle</h3>
            <div className="catalog-form-grid">
              <label>Kod<input required maxLength="50" value={listForm.code} onChange={(event) => setListForm({ ...listForm, code: event.target.value })} /></label>
              <label>Ad<input required maxLength="120" value={listForm.name} onChange={(event) => setListForm({ ...listForm, name: event.target.value })} /></label>
              <label>Müşteri grubu<select required value={listForm.customerGroupId} onChange={(event) => setListForm({ ...listForm, customerGroupId: event.target.value })}><option value="">Seçin</option>{activeGroups.map((group) => <option key={group.id} value={group.id}>{group.code} — {group.name}</option>)}</select></label>
              <label>Başlangıç<input required type="datetime-local" value={listForm.validFromUtc} onChange={(event) => setListForm({ ...listForm, validFromUtc: event.target.value })} /></label>
              <label>Bitiş (opsiyonel)<input type="datetime-local" value={listForm.validToUtc} onChange={(event) => setListForm({ ...listForm, validToUtc: event.target.value })} /></label>
              <label className="catalog-check"><input type="checkbox" checked={listForm.isActive} onChange={(event) => setListForm({ ...listForm, isActive: event.target.checked })} /> Aktif</label>
            </div>
            <button type="submit" className="add-button" disabled={Boolean(busy)}>Liste oluştur</button>
          </form>
          <div className="orders-table operations-table">
            <table><thead><tr><th>Kod</th><th>Ad</th><th>Grup</th><th>Geçerlilik</th><th>İşlem</th></tr></thead>
              <tbody>{pricing.lists.map((list) => <tr key={list.id}><td>{list.code}</td><td>{list.name}</td><td>{list.customerGroup}</td><td>{displayDate(list.validFromUtc)}<br /><small>{list.validToUtc ? displayDate(list.validToUtc) : 'Süresiz'} · {list.isActive ? 'Aktif' : 'Pasif'}</small></td><td><button type="button" className={list.isActive ? 'delete-btn' : 'edit-btn'} onClick={() => toggleList(list)} disabled={Boolean(busy)}>{list.isActive ? 'Pasifleştir' : 'Etkinleştir'}</button></td></tr>)}</tbody>
            </table>
          </div>

          <form className="admin-catalog-form" onSubmit={createRule}>
            <h3>Fiyat kuralı ekle</h3>
            <p className="admin-safety-note">İndirim yüzdesi veya sabit fiyat alanlarından yalnızca birini doldurun. Ürün/marka/kategori kimlikleri boş bırakılırsa kural daha genel olur.</p>
            <div className="catalog-form-grid">
              <label>Fiyat listesi<select required value={ruleForm.priceListId} onChange={(event) => setRuleForm({ ...ruleForm, priceListId: event.target.value })}><option value="">Seçin</option>{pricing.lists.map((list) => <option key={list.id} value={list.id}>{list.code} — {list.name}</option>)}</select></label>
              <label>Ürün ID<input type="number" min="1" value={ruleForm.productId} onChange={(event) => setRuleForm({ ...ruleForm, productId: event.target.value })} /></label>
              <label>Marka ID<input type="number" min="1" value={ruleForm.brandId} onChange={(event) => setRuleForm({ ...ruleForm, brandId: event.target.value })} /></label>
              <label>Kategori ID<input type="number" min="1" value={ruleForm.categoryId} onChange={(event) => setRuleForm({ ...ruleForm, categoryId: event.target.value })} /></label>
              <label>Minimum adet<input required type="number" min="1" value={ruleForm.minimumQuantity} onChange={(event) => setRuleForm({ ...ruleForm, minimumQuantity: event.target.value })} /></label>
              <label>Dönem cirosu<input required type="number" min="0" step="0.01" value={ruleForm.minimumPeriodRevenue} onChange={(event) => setRuleForm({ ...ruleForm, minimumPeriodRevenue: event.target.value })} /></label>
              <label>Öncelik<input required type="number" min="0" value={ruleForm.priority} onChange={(event) => setRuleForm({ ...ruleForm, priority: event.target.value })} /></label>
              <label>İndirim %<input type="number" min="0.01" max="99.99" step="0.01" value={ruleForm.discountPercentage} onChange={(event) => setRuleForm({ ...ruleForm, discountPercentage: event.target.value, fixedUnitPrice: '' })} /></label>
              <label>Sabit birim fiyat<input type="number" min="0.01" step="0.01" value={ruleForm.fixedUnitPrice} onChange={(event) => setRuleForm({ ...ruleForm, fixedUnitPrice: event.target.value, discountPercentage: '' })} /></label>
              <label>Başlangıç<input required type="datetime-local" value={ruleForm.validFromUtc} onChange={(event) => setRuleForm({ ...ruleForm, validFromUtc: event.target.value })} /></label>
              <label>Bitiş<input type="datetime-local" value={ruleForm.validToUtc} onChange={(event) => setRuleForm({ ...ruleForm, validToUtc: event.target.value })} /></label>
              <label className="catalog-check"><input type="checkbox" checked={ruleForm.isActive} onChange={(event) => setRuleForm({ ...ruleForm, isActive: event.target.checked })} /> Aktif</label>
            </div>
            <button type="submit" className="add-button" disabled={Boolean(busy)}>Kural oluştur</button>
          </form>
          <div className="orders-table operations-table payments-table">
            <table><thead><tr><th>Liste / kapsam</th><th>Eşik</th><th>Fiyat etkisi</th><th>Durum</th><th>İşlem</th></tr></thead>
              <tbody>{pricing.rules.map((rule) => <tr key={rule.id}><td>{rule.priceList}<br /><small>{rule.product || rule.brand || rule.category || 'Genel kural'}</small></td><td>{rule.minimumQuantity} adet<br /><small>{displayMoney(rule.minimumPeriodRevenue)}</small></td><td>{rule.discountPercentage != null ? `%${rule.discountPercentage}` : displayMoney(rule.fixedUnitPrice)}</td><td>{rule.isActive ? 'Aktif' : 'Pasif'}</td><td><button type="button" className={rule.isActive ? 'delete-btn' : 'edit-btn'} onClick={() => toggleRule(rule)} disabled={Boolean(busy)}>{rule.isActive ? 'Pasifleştir' : 'Etkinleştir'}</button></td></tr>)}</tbody>
            </table>
          </div>
        </section>
      )}

      {canQuote && (
        <section className="admin-catalog-form">
          <h2>Toplu teklif talepleri (RFQ)</h2>
          <div className="orders-table operations-table payments-table">
            <table><thead><tr><th>Talep</th><th>Durum</th><th>Satırlar</th><th>Geçerlilik</th><th>İşlem</th></tr></thead>
              <tbody>{quotes.map((quote) => <tr key={quote.id}><td>{quote.requestNumber}<br /><small>Kullanıcı #{quote.userId} · {displayDate(quote.createdAtUtc)}</small></td><td>{quote.status}</td><td>{quote.lines.map((line) => <div key={line.id}>{line.requestedIdentifier} × {line.requestedQuantity} — {line.status}{line.quotedUnitPrice != null ? ` / ${displayMoney(line.quotedUnitPrice, quote.currency)}` : ''}</div>)}</td><td>{displayDate(quote.quoteValidUntilUtc)}</td><td>{['Submitted', 'UnderReview'].includes(quote.status) ? <button type="button" className="edit-btn" onClick={() => prepareQuote(quote)} disabled={Boolean(busy)}>Teklif hazırla</button> : '—'}</td></tr>)}
                {quotes.length === 0 && <tr><td colSpan="5">RFQ kaydı yok.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {canSupply && (
        <section>
          <h2>Tedarikçi ve kaynak seçimi</h2>
          <form className="admin-catalog-form" onSubmit={createSupplier}>
            <h3>Tedarikçi ekle</h3>
            <div className="catalog-form-grid">
              <label>Kod<input required maxLength="50" value={supplierForm.code} onChange={(event) => setSupplierForm({ ...supplierForm, code: event.target.value })} /></label>
              <label>Ad<input required maxLength="120" value={supplierForm.name} onChange={(event) => setSupplierForm({ ...supplierForm, name: event.target.value })} /></label>
              <label>Sağlık<select value={supplierForm.healthStatus} onChange={(event) => setSupplierForm({ ...supplierForm, healthStatus: event.target.value })}><option value="Healthy">Sağlıklı</option><option value="Degraded">Kısıtlı</option><option value="Unhealthy">Sağlıksız</option></select></label>
              <label>Öncelik<input type="number" min="0" value={supplierForm.priority} onChange={(event) => setSupplierForm({ ...supplierForm, priority: event.target.value })} /></label>
              <label className="catalog-check"><input type="checkbox" checked={supplierForm.isActive} onChange={(event) => setSupplierForm({ ...supplierForm, isActive: event.target.checked })} /> Aktif</label>
            </div>
            <button type="submit" className="add-button" disabled={Boolean(busy)}>Tedarikçi oluştur</button>
          </form>
          <div className="orders-table operations-table payments-table">
            <table><thead><tr><th>Tedarikçi</th><th>Sağlık</th><th>Teklifler</th><th>İşlem</th></tr></thead>
              <tbody>{suppliers.map((supplier) => <tr key={supplier.id}><td>{supplier.code} — {supplier.name}<br /><small>Öncelik {supplier.priority} · {supplier.isActive ? 'Aktif' : 'Pasif'}</small></td><td>{supplier.healthStatus}</td><td>{supplier.offers.map((offer) => <div key={offer.id}>{offer.externalOfferId} · Ürün #{offer.productId} · {offer.availableQuantity} adet · {displayMoney(offer.unitCost, offer.currency)} <button type="button" className={offer.isActive ? 'delete-btn' : 'edit-btn'} onClick={() => toggleOffer(offer)} disabled={Boolean(busy)}>{offer.isActive ? 'Teklifi kapat' : 'Teklifi aç'}</button></div>)}</td><td><button type="button" className={supplier.isActive ? 'delete-btn' : 'edit-btn'} onClick={() => changeSupplier(supplier, { isActive: !supplier.isActive })} disabled={Boolean(busy)}>{supplier.isActive ? 'Pasifleştir' : 'Etkinleştir'}</button><select value={supplier.healthStatus} onChange={(event) => changeSupplier(supplier, { healthStatus: event.target.value })} disabled={Boolean(busy)}><option value="Healthy">Sağlıklı</option><option value="Degraded">Kısıtlı</option><option value="Unhealthy">Sağlıksız</option></select></td></tr>)}</tbody>
            </table>
          </div>

          <form className="admin-catalog-form" onSubmit={registerOffer}>
            <h3>Tedarikçi teklifi kaydet</h3>
            <p className="admin-safety-note">Ticari teklif satırı immutable’dır. Fiyat veya termin değişirse yeni dış teklif kimliğiyle kayıt açın; eski kaydı pasifleştirin.</p>
            <div className="catalog-form-grid">
              <label>Tedarikçi<select required value={offerForm.supplierId} onChange={(event) => setOfferForm({ ...offerForm, supplierId: event.target.value })}><option value="">Seçin</option>{suppliers.filter((supplier) => supplier.isActive).map((supplier) => <option key={supplier.id} value={supplier.id}>{supplier.code} — {supplier.name}</option>)}</select></label>
              <label>Dış teklif ID<input required maxLength="100" value={offerForm.externalOfferId} onChange={(event) => setOfferForm({ ...offerForm, externalOfferId: event.target.value })} /></label>
              <label>Ürün ID<input required type="number" min="1" value={offerForm.productId} onChange={(event) => setOfferForm({ ...offerForm, productId: event.target.value })} /></label>
              <label>OEM no<input required maxLength="80" value={offerForm.oemNumber} onChange={(event) => setOfferForm({ ...offerForm, oemNumber: event.target.value })} /></label>
              <label>Birim maliyet<input required type="number" min="0" step="0.0001" value={offerForm.unitCost} onChange={(event) => setOfferForm({ ...offerForm, unitCost: event.target.value })} /></label>
              <label>Nakliye maliyeti<input required type="number" min="0" step="0.0001" value={offerForm.shippingCost} onChange={(event) => setOfferForm({ ...offerForm, shippingCost: event.target.value })} /></label>
              <label>Mevcut adet<input required type="number" min="0" value={offerForm.availableQuantity} onChange={(event) => setOfferForm({ ...offerForm, availableQuantity: event.target.value })} /></label>
              <label>Termin (gün)<input required type="number" min="0" value={offerForm.leadTimeDays} onChange={(event) => setOfferForm({ ...offerForm, leadTimeDays: event.target.value })} /></label>
              <label>Minimum sipariş<input required type="number" min="1" value={offerForm.minimumOrderQuantity} onChange={(event) => setOfferForm({ ...offerForm, minimumOrderQuantity: event.target.value })} /></label>
              <label>Geçerlilik<input required type="datetime-local" value={offerForm.validUntilUtc} onChange={(event) => setOfferForm({ ...offerForm, validUntilUtc: event.target.value })} /></label>
              <label className="catalog-check"><input type="checkbox" checked={offerForm.canDropship} onChange={(event) => setOfferForm({ ...offerForm, canDropship: event.target.checked })} /> Dropship</label>
              <label className="catalog-check"><input type="checkbox" checked={offerForm.canSupplyWarehouse} onChange={(event) => setOfferForm({ ...offerForm, canSupplyWarehouse: event.target.checked })} /> Depoya tedarik</label>
            </div>
            <button type="submit" className="add-button" disabled={Boolean(busy)}>Teklifi kaydet</button>
          </form>

          <form className="admin-catalog-form" onSubmit={selectSource}>
            <h3>Kaynak seçimi simülasyonu</h3>
            <div className="catalog-form-grid">
              <label>Ürün ID<input required type="number" min="1" value={sourcingForm.productId} onChange={(event) => setSourcingForm({ ...sourcingForm, productId: event.target.value })} /></label>
              <label>Adet<input required type="number" min="1" value={sourcingForm.quantity} onChange={(event) => setSourcingForm({ ...sourcingForm, quantity: event.target.value })} /></label>
              <label>OEM no<input maxLength="80" value={sourcingForm.oemNumber} onChange={(event) => setSourcingForm({ ...sourcingForm, oemNumber: event.target.value })} /></label>
              <label className="catalog-check"><input type="checkbox" checked={sourcingForm.allowSplit} onChange={(event) => setSourcingForm({ ...sourcingForm, allowSplit: event.target.checked })} /> Bölünmüş tedarike izin ver</label>
              <label className="catalog-check"><input type="checkbox" checked={sourcingForm.requireDropship} onChange={(event) => setSourcingForm({ ...sourcingForm, requireDropship: event.target.checked })} /> Dropship zorunlu</label>
            </div>
            <button type="submit" className="add-button" disabled={Boolean(busy)}>Kaynak hesapla</button>
            {sourcingResult && <pre className="admin-safety-note">{JSON.stringify(sourcingResult, null, 2)}</pre>}
          </form>
        </section>
      )}
    </div>
  );
};

export default B2bAdminPanel;
