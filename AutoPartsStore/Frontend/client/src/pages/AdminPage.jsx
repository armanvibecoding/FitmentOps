import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { useAuth } from '../context/AuthContext';
import { adminAPI, categoriesAPI, brandsAPI, partBrandsAPI } from '../services/api';
import FitmentAdminPanel from '../components/FitmentAdminPanel';
import AuditAdminPanel from '../components/AuditAdminPanel';
import B2bAdminPanel from '../components/B2bAdminPanel';
import ChannelAdminPanel from '../components/ChannelAdminPanel';
import GarageAdminPanel from '../components/GarageAdminPanel';
import LegalAdminPanel from '../components/LegalAdminPanel';

const ORDER_STATUS_TRANSITIONS = {
  Pending: ['Processing', 'Cancelled'],
  Processing: ['Cancelled'],
  Shipped: [],
  Delivered: [],
  Cancelled: [],
};

const ORDER_STATUS_LABELS = {
  Pending: 'Bekliyor',
  Processing: 'Hazırlanıyor',
  Shipped: 'Kargoya verildi',
  Delivered: 'Teslim edildi',
  Cancelled: 'İptal edildi',
};

const PAYMENT_STATUS_LABELS = {
  Pending: 'Ödeme bekleniyor',
  Paid: 'Ödendi',
  Failed: 'Başarısız',
  Cancelled: 'İptal edildi',
  PartiallyRefunded: 'Kısmen iade edildi',
  Refunded: 'İade edildi',
};

const PAYMENT_METHOD_LABELS = {
  PayAtDelivery: 'Teslimatta ödeme',
};

const PAYMENT_PROVIDER_LABELS = {
  Manual: 'Manuel',
};

const SHIPMENT_STATUS_LABELS = {
  Created: 'Oluşturuldu',
  LabelPending: 'Etiket bekliyor',
  ReadyToShip: 'Sevke hazır',
  Shipped: 'Kargoya verildi',
  Delivered: 'Teslim edildi',
  Failed: 'Başarısız',
  Cancelled: 'İptal edildi',
};

const SHIPMENT_ACTIONS = {
  Created: [['label-pending', 'Etiket bekliyor'], ['ready-to-ship', 'Sevke hazır'], ['fail', 'Başarısız'], ['cancel', 'İptal']],
  LabelPending: [['ready-to-ship', 'Sevke hazır'], ['fail', 'Başarısız'], ['cancel', 'İptal']],
  ReadyToShip: [['ship', 'Kargoya ver'], ['fail', 'Başarısız'], ['cancel', 'İptal']],
  Shipped: [['deliver', 'Teslim edildi']],
};

const RETURN_STATUS_LABELS = {
  Requested: 'Talep edildi',
  Approved: 'Onaylandı',
  Rejected: 'Reddedildi',
  Received: 'Teslim alındı',
  Inspected: 'İncelendi',
  RefundPending: 'İade bekliyor',
  Refunded: 'İade edildi',
  Closed: 'Kapatıldı',
  Cancelled: 'İptal edildi',
};

const RETURN_ACTIONS = {
  Requested: [['approve', 'Onayla'], ['reject', 'Reddet'], ['cancel', 'İptal']],
  Approved: [['receive', 'Teslim al'], ['cancel', 'İptal']],
  Received: [['inspect', 'İncele']],
  Inspected: [['reject', 'Reddet']],
  Refunded: [['close', 'Kapat']],
};

const RETURN_REASON_CODES = [
  'defective',
  'damaged-in-transit',
  'wrong-item',
  'incompatible',
  'not-as-described',
  'unopened-withdrawal',
];

const ADMIN_TABS = [
  ['b2b', 'B2B ve tedarik'],
  ['dashboard', 'Dashboard'],
  ['products', 'Ürünler'],
  ['orders', 'Siparişler'],
  ['payments', 'Ödemeler'],
  ['shipments', 'Sevkiyatlar'],
  ['returns', 'İadeler'],
  ['integrations', 'Entegrasyonlar'],
  ['legal', 'Yasal metinler'],
  ['fitment', 'Araç uyumluluğu'],
  ['garage', 'Garaj ve bakım'],
  ['users', 'Kullanıcı ve roller'],
  ['audit', 'Audit kayıtları'],
];

const ALL_ADMIN_TABS = ADMIN_TABS.map(([key]) => key);
const ROLE_TABS = {
  admin: ALL_ADMIN_TABS,
  superadmin: ALL_ADMIN_TABS,
  finance: ['payments', 'b2b', 'integrations'],
  warehouse: ['orders', 'shipments', 'returns', 'b2b', 'integrations'],
  support: ['orders', 'returns', 'b2b', 'garage', 'integrations'],
  catalog: ['products', 'fitment', 'integrations'],
};

const formatCurrency = (amount, currency = 'TRY') =>
  new Intl.NumberFormat('tr-TR', {
    style: 'currency',
    currency,
  }).format(amount);

const formatDateTime = (value) =>
  value
    ? new Intl.DateTimeFormat('tr-TR', {
        dateStyle: 'short',
        timeStyle: 'short',
      }).format(new Date(value))
    : '-';

const INTEGRATION_CAPABILITIES = [
  ['payment', 'Online ödeme'],
  ['electronicInvoice', 'e-Fatura / e-Arşiv'],
  ['email', 'İşlemsel e-posta'],
  ['outboxDispatch', 'Outbox olay dağıtımı'],
  ['inventoryReservationExpiry', 'Stok rezervasyon süpürücüsü'],
  ['publicSite', 'Public origin ve SEO'],
  ['shippingCarrier', 'Kargo sağlayıcısı'],
];

const AdminPage = () => {
  const { user, loading } = useAuth();
  const navigate = useNavigate();
  const allowedTabs = useMemo(
    () => ROLE_TABS[user?.role?.toLowerCase()] || [],
    [user?.role]
  );
  const canAccess = useCallback((tab) => allowedTabs.includes(tab), [allowedTabs]);
  const [activeTab, setActiveTab] = useState('dashboard');
  const [stats, setStats] = useState(null);
  const [products, setProducts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [brands, setBrands] = useState([]);
  const [partBrands, setPartBrands] = useState([]);
  const [orders, setOrders] = useState([]);
  const [users, setUsers] = useState([]);
  const [payments, setPayments] = useState([]);
  const [paymentsLoading, setPaymentsLoading] = useState(false);
  const [paymentsError, setPaymentsError] = useState('');
  const [shipments, setShipments] = useState([]);
  const [returns, setReturns] = useState([]);
  const [integrationCapabilities, setIntegrationCapabilities] = useState(null);
  const [operationsLoading, setOperationsLoading] = useState(false);
  const [activeOperation, setActiveOperation] = useState('');
  const [operationMessage, setOperationMessage] = useState(null);
  const [updatingOrderId, setUpdatingOrderId] = useState(null);
  const [markingPaymentId, setMarkingPaymentId] = useState(null);
  const [showProductForm, setShowProductForm] = useState(false);
  const [editingProduct, setEditingProduct] = useState(null);
  const [productForm, setProductForm] = useState({
    name: '',
    description: '',
    brandId: '',
    partBrandId: '',
    partNumber: '',
    price: '',
    oldPrice: '',
    stock: '',
    imageUrl: '',
    categoryId: '',
    isFeatured: false,
    isNew: false,
    discountPercentage: '',
    badgeText: '',
  });

  const fetchData = useCallback(async () => {
    try {
      if (canAccess('products') || canAccess('fitment')) {
        const [categoriesRes, brandsRes, partBrandsRes, productsRes] = await Promise.all([
          categoriesAPI.getAll(),
          brandsAPI.getAll(),
          partBrandsAPI.getAll(),
          adminAPI.getAllProducts(),
        ]);
        setCategories(categoriesRes.data);
        setBrands(brandsRes.data);
        setPartBrands(partBrandsRes.data);
        setProducts(productsRes.data);
      }

      if (canAccess('dashboard')) {
        const statsRes = await adminAPI.getStats();
        setStats(statsRes.data);
      }

      if (canAccess('orders') || canAccess('shipments') || canAccess('returns')) {
        const ordersRes = await adminAPI.getAllOrders();
        setOrders(ordersRes.data);
      }

      if (canAccess('users')) {
        const usersRes = await adminAPI.getAllUsers();
        setUsers(usersRes.data);
      }
    } catch (error) {
      console.error('Error fetching admin data:', error);
      console.error('Error details:', error.response?.data);
      if (error.response?.status === 401) {
        alert('Oturum süreniz dolmuş. Lütfen tekrar giriş yapın.');
        navigate('/login');
      }
    }
  }, [canAccess, navigate]);

  const fetchPayments = useCallback(async () => {
    if (!canAccess('payments')) return;
    setPaymentsLoading(true);
    setPaymentsError('');

    try {
      const response = await adminAPI.getAllPayments();
      setPayments(response.data);
    } catch (error) {
      setPaymentsError(
        error.response?.data?.message || 'Ödeme kayıtları yüklenemedi.'
      );

      if (error.response?.status === 401) {
        navigate('/login');
      }
    } finally {
      setPaymentsLoading(false);
    }
  }, [canAccess, navigate]);

  const fetchOperations = useCallback(async () => {
    if (!canAccess('shipments') && !canAccess('returns') && !canAccess('integrations')) return;
    setOperationsLoading(true);
    try {
      const requests = [];
      if (canAccess('shipments')) requests.push(['shipments', adminAPI.getShipments()]);
      if (canAccess('returns')) requests.push(['returns', adminAPI.getReturns()]);
      if (canAccess('integrations')) requests.push(['integrations', adminAPI.getIntegrationCapabilities()]);
      const responses = await Promise.all(requests.map(async ([key, request]) => [key, await request]));
      responses.forEach(([key, response]) => {
        if (key === 'shipments') setShipments(response.data);
        if (key === 'returns') setReturns(response.data);
        if (key === 'integrations') setIntegrationCapabilities(response.data);
      });
    } catch (error) {
      setOperationMessage({
        type: 'error',
        text: error.response?.data?.message || 'Operasyon kayıtları yüklenemedi.',
      });
      if (error.response?.status === 401) navigate('/login');
    } finally {
      setOperationsLoading(false);
    }
  }, [canAccess, navigate]);

  useEffect(() => {
    if (loading) return;

    if (!user || allowedTabs.length === 0) {
      navigate('/login');
      return;
    }

    setActiveTab((current) => allowedTabs.includes(current) ? current : allowedTabs[0]);

    fetchData();
    fetchPayments();
    fetchOperations();
  }, [user, navigate, loading, allowedTabs, fetchData, fetchPayments, fetchOperations]);

  const handleShipmentAction = async (shipment, action) => {
    let data;
    if (action === 'ship') {
      const carrier = window.prompt('Kargo firması kodu/adı:');
      if (!carrier) return;
      const trackingNumber = window.prompt('Takip numarası:');
      if (!trackingNumber) return;
      data = { carrier, trackingNumber };
    }

    setActiveOperation(`shipment-${shipment.id}`);
    setOperationMessage(null);
    try {
      await adminAPI.transitionShipment(shipment.id, action, data);
      await fetchOperations();
      setOperationMessage({ type: 'success', text: 'Sevkiyat durumu güncellendi.' });
    } catch (error) {
      setOperationMessage({
        type: 'error',
        text: error.response?.data?.message || 'Sevkiyat güncellenemedi.',
      });
    } finally {
      setActiveOperation('');
    }
  };

  const handleReturnAction = async (returnRequest, action) => {
    setActiveOperation(`return-${returnRequest.id}`);
    setOperationMessage(null);
    try {
      await adminAPI.transitionReturn(returnRequest.id, action);
      await fetchOperations();
      setOperationMessage({ type: 'success', text: 'İade talebi güncellendi.' });
    } catch (error) {
      setOperationMessage({
        type: 'error',
        text: error.response?.data?.message || 'İade talebi güncellenemedi.',
      });
    } finally {
      setActiveOperation('');
    }
  };

  const getShipmentCandidates = (order) => {
    const allocatedByOrderItem = new Map();
    shipments
      .filter((shipment) =>
        shipment.orderId === order.id && !['Cancelled', 'Failed'].includes(shipment.status)
      )
      .flatMap((shipment) => shipment.items)
      .forEach((item) => {
        allocatedByOrderItem.set(
          item.orderItemId,
          (allocatedByOrderItem.get(item.orderItemId) || 0) + item.quantity
        );
      });

    return order.orderItems
      .map((item) => ({
        ...item,
        remainingQuantity: Math.max(
          0,
          item.quantity - (allocatedByOrderItem.get(item.id) || 0)
        ),
      }))
      .filter((item) => item.remainingQuantity > 0);
  };

  const getRetryIdentity = (operation, orderId, items) => {
    const canonicalItems = [...items].sort((left, right) =>
      left.orderItemId - right.orderItemId
    );
    const storageKey = `admin-operation:${operation}:${orderId}:${JSON.stringify(canonicalItems)}`;
    const existingKey = window.sessionStorage.getItem(storageKey);
    if (existingKey) return { idempotencyKey: existingKey, storageKey };

    const idempotencyKey = window.crypto.randomUUID();
    window.sessionStorage.setItem(storageKey, idempotencyKey);
    return { idempotencyKey, storageKey };
  };

  const handleCreateShipment = async (order) => {
    const items = [];
    for (const item of getShipmentCandidates(order)) {
      const answer = window.prompt(
        `${item.product?.name || `Kalem ${item.id}`} için sevk adedi (0 = atla, kalan ${item.remainingQuantity}):`,
        '0'
      );
      if (answer === null) return;
      const quantity = Number.parseInt(answer, 10);
      if (Number.isInteger(quantity) && quantity > 0 && quantity <= item.remainingQuantity) {
        items.push({ orderItemId: item.id, quantity });
      } else if (quantity > item.remainingQuantity) {
        setOperationMessage({
          type: 'error',
          text: `${item.product?.name || `Kalem ${item.id}`} için kalan miktar aşılamaz.`,
        });
        return;
      }
    }
    if (items.length === 0) {
      setOperationMessage({ type: 'error', text: 'En az bir sevkiyat kalemi seçilmelidir.' });
      return;
    }

    setActiveOperation(`create-shipment-${order.id}`);
    const retry = getRetryIdentity('shipment', order.id, items);
    try {
      await adminAPI.createShipment(order.id, items, retry.idempotencyKey);
      window.sessionStorage.removeItem(retry.storageKey);
      await fetchOperations();
      setOperationMessage({ type: 'success', text: 'Sevkiyat oluşturuldu.' });
    } catch (error) {
      setOperationMessage({ type: 'error', text: error.response?.data?.message || 'Sevkiyat oluşturulamadı.' });
    } finally {
      setActiveOperation('');
    }
  };

  const handleCreateReturn = async (order) => {
    const items = [];
    for (const item of order.orderItems) {
      const answer = window.prompt(
        `${item.product?.name || `Kalem ${item.id}`} için iade adedi (0 = atla, en fazla ${item.quantity}):`,
        '0'
      );
      if (answer === null) return;
      const quantity = Number.parseInt(answer, 10);
      if (Number.isInteger(quantity) && quantity > 0) {
        const reasonCode = window.prompt(
          `Neden kodu: ${RETURN_REASON_CODES.join(', ')}`,
          RETURN_REASON_CODES[0]
        );
        if (!reasonCode) return;
        items.push({ orderItemId: item.id, quantity, reasonCode });
      }
    }
    if (items.length === 0) {
      setOperationMessage({ type: 'error', text: 'En az bir iade kalemi seçilmelidir.' });
      return;
    }

    setActiveOperation(`create-return-${order.id}`);
    const retry = getRetryIdentity('return', order.id, items);
    try {
      await adminAPI.createReturn(order.id, items, retry.idempotencyKey);
      window.sessionStorage.removeItem(retry.storageKey);
      await fetchOperations();
      setOperationMessage({ type: 'success', text: 'İade talebi oluşturuldu.' });
    } catch (error) {
      setOperationMessage({ type: 'error', text: error.response?.data?.message || 'İade talebi oluşturulamadı.' });
    } finally {
      setActiveOperation('');
    }
  };

  const handleProductSubmit = async (e) => {
    e.preventDefault();

    const productData = {
      ...productForm,
      price: parseFloat(productForm.price),
      oldPrice: productForm.oldPrice ? parseFloat(productForm.oldPrice) : null,
      stock: parseInt(productForm.stock),
      categoryId: parseInt(productForm.categoryId),
      brandId: parseInt(productForm.brandId),
      partBrandId: parseInt(productForm.partBrandId),
      discountPercentage: productForm.discountPercentage ? parseInt(productForm.discountPercentage) : null,
    };

    try {
      if (editingProduct) {
        await adminAPI.updateProduct(editingProduct.id, productData);
        alert('Ürün başarıyla güncellendi!');
      } else {
        await adminAPI.createProduct(productData);
        alert('Ürün başarıyla eklendi!');
      }

      setShowProductForm(false);
      setEditingProduct(null);
      resetProductForm();
      fetchData();
    } catch (error) {
      alert('Hata: ' + (error.response?.data?.message || 'İşlem başarısız'));
    }
  };

  const handleEditProduct = (product) => {
    setEditingProduct(product);
    setProductForm({
      name: product.name,
      description: product.description,
      brandId: product.brandId.toString(),
      partBrandId: product.partBrandId.toString(),
      partNumber: product.partNumber,
      price: product.price.toString(),
      oldPrice: product.oldPrice?.toString() || '',
      stock: product.stock.toString(),
      imageUrl: product.imageUrl,
      categoryId: product.categoryId.toString(),
      isFeatured: product.isFeatured,
      isNew: product.isNew,
      discountPercentage: product.discountPercentage?.toString() || '',
      badgeText: product.badgeText || '',
    });
    setShowProductForm(true);
  };

  const handleDeleteProduct = async (id) => {
    if (!confirm('Bu ürünü silmek istediğinizden emin misiniz?')) return;

    try {
      await adminAPI.deleteProduct(id);
      alert('Ürün silindi!');
      fetchData();
    } catch (error) {
      alert('Silme hatası: ' + error.message);
    }
  };

  const handleOrderStatusChange = async (orderId, status) => {
    setUpdatingOrderId(orderId);
    setOperationMessage(null);

    try {
      await adminAPI.updateOrderStatus(orderId, status);
      await fetchData();
      setOperationMessage({ type: 'success', text: 'Sipariş durumu güncellendi.' });
    } catch (error) {
      setOperationMessage({
        type: 'error',
        text: error.response?.data?.message || 'Sipariş durumu güncellenemedi.',
      });
    } finally {
      setUpdatingOrderId(null);
    }
  };

  const handleMarkPaymentPaid = async (paymentId) => {
    if (!confirm('Teslimatta ödemenin tahsil edildiğini onaylıyor musunuz?')) return;

    setMarkingPaymentId(paymentId);
    setOperationMessage(null);

    try {
      await adminAPI.markPaymentPaid(paymentId);
      await Promise.all([fetchData(), fetchPayments()]);
      setOperationMessage({ type: 'success', text: 'Ödeme tahsil edildi olarak işaretlendi.' });
    } catch (error) {
      setOperationMessage({
        type: 'error',
        text: error.response?.data?.message || 'Ödeme durumu güncellenemedi.',
      });
    } finally {
      setMarkingPaymentId(null);
    }
  };

  const handleUserRoleChange = async (managedUser, role) => {
    if (!window.confirm(`${managedUser.email} kullanıcısının rolü ${role} olarak değiştirilsin mi?`)) return;

    setActiveOperation(`user-role-${managedUser.id}`);
    setOperationMessage(null);
    try {
      await adminAPI.updateUserRole(managedUser.id, role);
      const response = await adminAPI.getAllUsers();
      setUsers(response.data);
      setOperationMessage({ type: 'success', text: 'Kullanıcı rolü güncellendi.' });
    } catch (error) {
      setOperationMessage({
        type: 'error',
        text: error.response?.data?.message || 'Kullanıcı rolü güncellenemedi.',
      });
    } finally {
      setActiveOperation('');
    }
  };

  const resetProductForm = () => {
    setProductForm({
      name: '',
      description: '',
      brandId: '',
      partBrandId: '',
      partNumber: '',
      price: '',
      oldPrice: '',
      stock: '',
      imageUrl: '',
      categoryId: '',
      isFeatured: false,
      isNew: false,
      discountPercentage: '',
      badgeText: '',
    });
  };

  if (loading) {
    return (
      <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '80vh' }}>
        <p>Yükleniyor...</p>
      </div>
    );
  }

  if (!user || allowedTabs.length === 0) {
    return null;
  }

  return (
    <div className="admin-page">
      <div className="admin-sidebar">
        <h2>Admin Panel</h2>
        <ul>
          {ADMIN_TABS.filter(([key]) => allowedTabs.includes(key)).map(([key, label]) => (
            <li key={key} className={activeTab === key ? 'active' : ''} onClick={() => setActiveTab(key)}>
              {label}
            </li>
          ))}
        </ul>
      </div>

      <div className="admin-content">
        {operationMessage && (
          <div
            className={`admin-operation-message ${operationMessage.type}`}
            role={operationMessage.type === 'error' ? 'alert' : 'status'}
          >
            {operationMessage.text}
          </div>
        )}

        {activeTab === 'dashboard' && stats && (
          <div className="dashboard">
            <h1>Dashboard</h1>
            <div className="stats-grid">
              <div className="stat-card">
                <h3>Toplam Ürün</h3>
                <p className="stat-number">{stats.totalProducts}</p>
              </div>
              <div className="stat-card">
                <h3>Toplam Sipariş</h3>
                <p className="stat-number">{stats.totalOrders}</p>
              </div>
              <div className="stat-card">
                <h3>Toplam Kullanıcı</h3>
                <p className="stat-number">{stats.totalUsers}</p>
              </div>
              <div className="stat-card">
                <h3>Net Gelir</h3>
                <p className="stat-number">{formatCurrency(stats.totalRevenue)}</p>
              </div>
              <div className="stat-card">
                <h3>Brüt Tahsilat</h3>
                <p className="stat-number">{formatCurrency(stats.grossRevenue)}</p>
              </div>
              <div className="stat-card">
                <h3>İade Edilen</h3>
                <p className="stat-number">{formatCurrency(stats.refundedAmount)}</p>
              </div>
              <div className="stat-card">
                <h3>Bekleyen Sipariş</h3>
                <p className="stat-number">{stats.pendingOrders}</p>
              </div>
              <div className="stat-card">
                <h3>Bekleyen Ödeme</h3>
                <p className="stat-number">{stats.pendingPayments}</p>
              </div>
            </div>
          </div>
        )}

        {activeTab === 'products' && (
          <div className="products-management">
            <div className="management-header">
              <h1>Ürün Yönetimi</h1>
              <button
                className="add-button"
                onClick={() => {
                  setEditingProduct(null);
                  resetProductForm();
                  setShowProductForm(true);
                }}
              >
                + Yeni Ürün Ekle
              </button>
            </div>

            {showProductForm && categories.length > 0 && brands.length > 0 && partBrands.length > 0 && (
              <div className="product-form-modal">
                <div className="modal-content">
                  <h2>{editingProduct ? 'Ürün Düzenle' : 'Yeni Ürün Ekle'}</h2>
                  <form onSubmit={handleProductSubmit}>
                    <div className="form-grid">
                      <input
                        type="text"
                        placeholder="Ürün Adı"
                        value={productForm.name}
                        onChange={(e) => setProductForm({ ...productForm, name: e.target.value })}
                        required
                      />
                      <select
                        value={productForm.brandId}
                        onChange={(e) => setProductForm({ ...productForm, brandId: e.target.value })}
                        required
                      >
                        <option value="">Araç Markası Seçin</option>
                        {brands.map((brand) => (
                          <option key={brand.id} value={brand.id}>
                            {brand.name}
                          </option>
                        ))}
                      </select>
                      <select
                        value={productForm.partBrandId}
                        onChange={(e) => setProductForm({ ...productForm, partBrandId: e.target.value })}
                        required
                      >
                        <option value="">Parça Markası Seçin</option>
                        {partBrands.map((partBrand) => (
                          <option key={partBrand.id} value={partBrand.id}>
                            {partBrand.name}
                          </option>
                        ))}
                      </select>
                      <input
                        type="text"
                        placeholder="Parça Numarası"
                        value={productForm.partNumber}
                        onChange={(e) => setProductForm({ ...productForm, partNumber: e.target.value })}
                        required
                      />
                      <input
                        type="number"
                        step="0.01"
                        placeholder="Fiyat"
                        value={productForm.price}
                        onChange={(e) => setProductForm({ ...productForm, price: e.target.value })}
                        required
                      />
                      <input
                        type="number"
                        step="0.01"
                        placeholder="Eski Fiyat (Opsiyonel)"
                        value={productForm.oldPrice}
                        onChange={(e) => setProductForm({ ...productForm, oldPrice: e.target.value })}
                      />
                      <input
                        type="number"
                        placeholder="Stok"
                        value={productForm.stock}
                        onChange={(e) => setProductForm({ ...productForm, stock: e.target.value })}
                        required
                      />
                      <select
                        value={productForm.categoryId}
                        onChange={(e) => {
                          console.log('Selected category:', e.target.value);
                          setProductForm({ ...productForm, categoryId: e.target.value });
                        }}
                        required
                      >
                        <option value="">Kategori Seçin ({categories.length} kategori mevcut)</option>
                        {categories.map((cat) => (
                          <option key={cat.id} value={cat.id}>
                            {cat.name}
                          </option>
                        ))}
                      </select>
                      <input
                        type="text"
                        placeholder="Resim URL"
                        value={productForm.imageUrl}
                        onChange={(e) => setProductForm({ ...productForm, imageUrl: e.target.value })}
                      />
                    </div>
                    <textarea
                      placeholder="Açıklama"
                      value={productForm.description}
                      onChange={(e) => setProductForm({ ...productForm, description: e.target.value })}
                      required
                      rows="3"
                    />
                    <div className="form-checkboxes">
                      <label>
                        <input
                          type="checkbox"
                          checked={productForm.isFeatured}
                          onChange={(e) => setProductForm({ ...productForm, isFeatured: e.target.checked })}
                        />
                        Öne Çıkan
                      </label>
                      <label>
                        <input
                          type="checkbox"
                          checked={productForm.isNew}
                          onChange={(e) => setProductForm({ ...productForm, isNew: e.target.checked })}
                        />
                        Yeni Ürün
                      </label>
                    </div>
                    <div className="form-actions">
                      <button type="submit" className="save-button">
                        {editingProduct ? 'Güncelle' : 'Ekle'}
                      </button>
                      <button
                        type="button"
                        className="cancel-button"
                        onClick={() => {
                          setShowProductForm(false);
                          setEditingProduct(null);
                          resetProductForm();
                        }}
                      >
                        İptal
                      </button>
                    </div>
                  </form>
                </div>
              </div>
            )}

            <div className="products-table">
              <table>
                <thead>
                  <tr>
                    <th>ID</th>
                    <th>Ürün Adı</th>
                    <th>Marka</th>
                    <th>Fiyat</th>
                    <th>Stok</th>
                    <th>İşlemler</th>
                  </tr>
                </thead>
                <tbody>
                  {products.map((product) => (
                    <tr key={product.id}>
                      <td>{product.id}</td>
                      <td>{product.name}</td>
                      <td>{product.brand?.name || product.brand}</td>
                      <td>{product.price.toFixed(2)} TL</td>
                      <td>{product.stock}</td>
                      <td>
                        <button onClick={() => handleEditProduct(product)} className="edit-btn">
                          Düzenle
                        </button>
                        <button onClick={() => handleDeleteProduct(product.id)} className="delete-btn">
                          Sil
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {activeTab === 'orders' && (
          <div className="orders-management">
            <h1>Sipariş Yönetimi</h1>
            <div className="orders-table">
              <table>
                <thead>
                  <tr>
                    <th>Sipariş No</th>
                    <th>Müşteri</th>
                    <th>Tutar</th>
                    <th>Durum</th>
                    <th>Ödeme</th>
                    <th>Tarih</th>
                  </tr>
                </thead>
                <tbody>
                  {orders.map((order) => (
                    <tr key={order.id}>
                      <td>{order.orderNumber}</td>
                      <td>{order.customerName}</td>
                      <td>{order.totalAmount.toFixed(2)} TL</td>
                      <td>
                        <span className={`status-badge status-${order.status.toLowerCase()}`}>
                          {ORDER_STATUS_LABELS[order.status] || 'Bilinmiyor'}
                        </span>
                        {(ORDER_STATUS_TRANSITIONS[order.status]?.length || 0) > 0 && (
                          <select
                            aria-label={`${order.orderNumber} durumunu güncelle`}
                            value=""
                            disabled={updatingOrderId === order.id}
                            onChange={(event) => {
                              if (event.target.value) {
                                handleOrderStatusChange(order.id, event.target.value);
                              }
                            }}
                          >
                            <option value="">Durum değiştir</option>
                            {ORDER_STATUS_TRANSITIONS[order.status].map((status) => (
                              <option key={status} value={status}>
                                {ORDER_STATUS_LABELS[status]}
                              </option>
                            ))}
                          </select>
                        )}
                      </td>
                      <td>
                        {order.payment
                          ? PAYMENT_STATUS_LABELS[order.payment.status] || 'Durum bilinmiyor'
                          : 'Ödeme kaydı yok'}
                      </td>
                      <td>{new Date(order.orderDate).toLocaleDateString('tr-TR')}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {activeTab === 'payments' && (
          <div className="payments-management">
            <div className="management-header">
              <h1>Ödeme Yönetimi</h1>
              <button
                type="button"
                className="add-button"
                onClick={fetchPayments}
                disabled={paymentsLoading}
              >
                {paymentsLoading ? 'Yenileniyor…' : 'Yenile'}
              </button>
            </div>

            {paymentsError && (
              <div className="admin-operation-message error" role="alert">
                {paymentsError}
              </div>
            )}

            {!paymentsLoading && !paymentsError && payments.length === 0 && (
              <p>Henüz ödeme kaydı bulunmuyor.</p>
            )}

            {payments.length > 0 && (
              <div className="orders-table payments-table">
                <table>
                  <thead>
                    <tr>
                      <th>Sipariş No</th>
                      <th>E-posta</th>
                      <th>Sağlayıcı</th>
                      <th>Yöntem</th>
                      <th>Durum</th>
                      <th>Tutar</th>
                      <th>İade</th>
                      <th>Oluşturuldu</th>
                      <th>Güncellendi</th>
                      <th>Ödendi</th>
                      <th>İşlem</th>
                    </tr>
                  </thead>
                  <tbody>
                    {payments.map((payment) => {
                      const canMarkPaid =
                        payment.status === 'Pending' && payment.method === 'PayAtDelivery';

                      return (
                        <tr key={payment.id}>
                          <td>{payment.orderNumber}</td>
                          <td>{payment.customerEmail}</td>
                          <td>{PAYMENT_PROVIDER_LABELS[payment.provider] || payment.provider}</td>
                          <td>{PAYMENT_METHOD_LABELS[payment.method] || payment.method}</td>
                          <td>
                            <span className={`status-badge status-${payment.status.toLowerCase()}`}>
                              {PAYMENT_STATUS_LABELS[payment.status] || 'Durum bilinmiyor'}
                            </span>
                          </td>
                          <td>{formatCurrency(payment.amount, payment.currency)}</td>
                          <td>
                            {formatCurrency(payment.refundedAmount, payment.currency)}
                            {payment.pendingRefundAmount > 0 && (
                              <small className="payment-refund-pending">
                                {` (${formatCurrency(payment.pendingRefundAmount, payment.currency)} bekliyor)`}
                              </small>
                            )}
                          </td>
                          <td>{formatDateTime(payment.createdAt)}</td>
                          <td>{formatDateTime(payment.updatedAt)}</td>
                          <td>{formatDateTime(payment.paidAt)}</td>
                          <td>
                            {canMarkPaid ? (
                              <button
                                type="button"
                                className="edit-btn"
                                disabled={markingPaymentId === payment.id}
                                onClick={() => handleMarkPaymentPaid(payment.id)}
                              >
                                {markingPaymentId === payment.id ? 'İşleniyor…' : 'Tahsil edildi'}
                              </button>
                            ) : (
                              '—'
                            )}
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {activeTab === 'shipments' && (
          <div className="shipments-management">
            <div className="management-header">
              <h1>Sevkiyat Yönetimi</h1>
              <button type="button" className="add-button" onClick={fetchOperations} disabled={operationsLoading}>
                {operationsLoading ? 'Yenileniyor…' : 'Yenile'}
              </button>
            </div>
            <h2>Sevkiyata uygun siparişler</h2>
            <div className="operation-card-grid">
              {orders.filter((order) =>
                ['Pending', 'Processing'].includes(order.status) &&
                getShipmentCandidates(order).length > 0
              ).map((order) => (
                <article className="operation-card" key={order.id}>
                  <strong>{order.orderNumber}</strong>
                  <span>{getShipmentCandidates(order).length} sevk bekleyen kalem</span>
                  <button
                    type="button"
                    className="edit-btn"
                    disabled={activeOperation === `create-shipment-${order.id}`}
                    onClick={() => handleCreateShipment(order)}
                  >
                    Sevkiyat oluştur
                  </button>
                </article>
              ))}
            </div>
            <h2>Sevkiyat kayıtları</h2>
            {shipments.length === 0 ? <p>Henüz sevkiyat bulunmuyor.</p> : (
              <div className="orders-table operations-table">
                <table>
                  <thead>
                    <tr><th>Sipariş</th><th>Kalemler</th><th>Durum</th><th>Kargo / Takip</th><th>Güncellendi</th><th>İşlemler</th></tr>
                  </thead>
                  <tbody>
                    {shipments.map((shipment) => (
                      <tr key={shipment.id}>
                        <td>{shipment.orderNumber}</td>
                        <td>{shipment.items.map((item) => `${item.partNumber} × ${item.quantity}`).join(', ')}</td>
                        <td><span className={`status-badge status-${shipment.status.toLowerCase()}`}>{SHIPMENT_STATUS_LABELS[shipment.status] || shipment.status}</span></td>
                        <td>{shipment.carrier && shipment.trackingNumber ? `${shipment.carrier} / ${shipment.trackingNumber}` : '—'}</td>
                        <td>{formatDateTime(shipment.updatedAt)}</td>
                        <td className="operation-actions">
                          {(SHIPMENT_ACTIONS[shipment.status] || []).map(([action, label]) => (
                            <button key={action} type="button" className="edit-btn" disabled={activeOperation === `shipment-${shipment.id}`} onClick={() => handleShipmentAction(shipment, action)}>{label}</button>
                          ))}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {activeTab === 'returns' && (
          <div className="returns-management">
            <div className="management-header">
              <h1>İade / RMA Yönetimi</h1>
              <button type="button" className="add-button" onClick={fetchOperations} disabled={operationsLoading}>
                {operationsLoading ? 'Yenileniyor…' : 'Yenile'}
              </button>
            </div>
            <h2>İadeye uygun siparişler</h2>
            <div className="operation-card-grid">
              {orders.filter((order) => order.status === 'Delivered').map((order) => (
                <article className="operation-card" key={order.id}>
                  <strong>{order.orderNumber}</strong>
                  <span>{order.orderItems.length} kalem</span>
                  <button type="button" className="edit-btn" disabled={activeOperation === `create-return-${order.id}`} onClick={() => handleCreateReturn(order)}>İade talebi oluştur</button>
                </article>
              ))}
            </div>
            <h2>İade kayıtları</h2>
            {returns.length === 0 ? <p>Henüz iade talebi bulunmuyor.</p> : (
              <div className="orders-table operations-table">
                <table>
                  <thead>
                    <tr><th>Sipariş</th><th>Kalemler</th><th>Durum</th><th>Talep</th><th>İade tarihi</th><th>İşlemler</th></tr>
                  </thead>
                  <tbody>
                    {returns.map((returnRequest) => (
                      <tr key={returnRequest.id}>
                        <td>{returnRequest.orderNumber}</td>
                        <td>{returnRequest.items.map((item) => `${item.partNumber} × ${item.quantity} (${item.reasonCode})`).join(', ')}</td>
                        <td><span className={`status-badge status-${returnRequest.status.toLowerCase()}`}>{RETURN_STATUS_LABELS[returnRequest.status] || returnRequest.status}</span></td>
                        <td>{formatDateTime(returnRequest.requestedAt)}</td>
                        <td>{formatDateTime(returnRequest.refundedAt)}</td>
                        <td className="operation-actions">
                          {(RETURN_ACTIONS[returnRequest.status] || []).map(([action, label]) => (
                            <button key={action} type="button" className="edit-btn" disabled={activeOperation === `return-${returnRequest.id}`} onClick={() => handleReturnAction(returnRequest, action)}>{label}</button>
                          ))}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <p className="admin-safety-note">RefundPending ve Refunded durumları burada elle verilemez; yalnız doğrulanmış ödeme sağlayıcı sonucu ile ilerletilir.</p>
          </div>
        )}

        {activeTab === 'integrations' && (
          <div className="integrations-management">
            <ChannelAdminPanel role={user.role} />
            <h1>Entegrasyon Hazırlığı</h1>
            {!integrationCapabilities ? <p>Yükleniyor…</p> : (
              <div className="operation-card-grid">
                {INTEGRATION_CAPABILITIES.map(([key, label]) => {
                  const capability = integrationCapabilities[key];
                  if (!capability) return null;
                  return (
                    <article className="operation-card" key={key}>
                      <strong>{label}</strong>
                      <span>Sağlayıcı: {capability.provider}</span>
                      <span>Mod: {capability.mode}</span>
                      <span>Sağlık: {capability.healthStatus}</span>
                      <span>Engel: {capability.blockingReason}</span>
                      <span>{capability.liveReady ? 'Canlıya hazır' : 'Canlıya hazır değil'}</span>
                    </article>
                  );
                })}
              </div>
            )}
            <p className="admin-safety-note">Bu ekran yalnız yetenek durumunu gösterir; credential, anahtar veya imza verisi API üzerinden dönmez.</p>
          </div>
        )}

        {activeTab === 'fitment' && <FitmentAdminPanel products={products} />}

        {activeTab === 'garage' && <GarageAdminPanel />}

        {activeTab === 'legal' && <LegalAdminPanel role={user.role} />}

        {activeTab === 'b2b' && <B2bAdminPanel role={user.role} />}

        {activeTab === 'users' && (
          <div className="users-management">
            <div className="management-header"><h1>Kullanıcı ve rol yönetimi</h1></div>
            <div className="orders-table operations-table">
              <table>
                <thead><tr><th>Kullanıcı</th><th>Durum</th><th>Oluşturma</th><th>Rol</th></tr></thead>
                <tbody>
                  {users.map((managedUser) => (
                    <tr key={managedUser.id}>
                      <td><strong>{managedUser.fullName}</strong><br /><span>{managedUser.email}</span></td>
                      <td>{managedUser.isActive ? 'Aktif' : 'Devre dışı'}</td>
                      <td>{formatDateTime(managedUser.createdAt)}</td>
                      <td>
                        <select
                          aria-label={`${managedUser.email} rolü`}
                          value={managedUser.role}
                          disabled={activeOperation === `user-role-${managedUser.id}`}
                          onChange={(event) => handleUserRoleChange(managedUser, event.target.value)}
                        >
                          <option value="User">Müşteri</option>
                          <option value="Admin">Legacy Admin</option>
                          <option value="finance">Finans</option>
                          <option value="warehouse">Depo</option>
                          <option value="catalog">Katalog</option>
                          <option value="support">Destek</option>
                          <option value="superadmin">SuperAdmin</option>
                        </select>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="admin-safety-note">Sistem son aktif Admin/SuperAdmin hesabının yetkisini düşürmez. Her başarılı veya tekrarlanan rol işlemi audit zincirine yazılır.</p>
          </div>
        )}

        {activeTab === 'audit' && <AuditAdminPanel />}
      </div>
    </div>
  );
};

export default AdminPage;
