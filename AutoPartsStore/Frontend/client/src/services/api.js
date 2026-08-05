import axios from 'axios';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5167/api';

const api = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

// Token'ı otomatik olarak tüm isteklere ekle
api.interceptors.request.use(
  (config) => {
    const token = localStorage.getItem('token');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

export const categoriesAPI = {
  getAll: () => api.get('/categories'),
  getById: (id) => api.get(`/categories/${id}`),
  getBySlug: (slug) => api.get(`/categories/slug/${slug}`),
};

export const brandsAPI = {
  getAll: () => api.get('/brands'),
  getById: (id) => api.get(`/brands/${id}`),
  getBySlug: (slug) => api.get(`/brands/slug/${slug}`),
};

export const partBrandsAPI = {
  getAll: () => api.get('/partbrands'),
  getById: (id) => api.get(`/partbrands/${id}`),
  getBySlug: (slug) => api.get(`/partbrands/slug/${slug}`),
};

export const productsAPI = {
  getAll: (params) => api.get('/products', { params }),
  getById: (id) => api.get(`/products/${id}`),
  getFeatured: () => api.get('/products/featured'),
  search: (query) => api.get(`/products/search?query=${query}`),
};

export const ordersAPI = {
  create: (orderData, idempotencyKey) => api.post('/orders', orderData, {
    headers: { 'Idempotency-Key': idempotencyKey },
  }),
  startHostedCheckout: (checkoutData, idempotencyKey) =>
    api.post('/orders/hosted-checkout', checkoutData, {
      headers: { 'Idempotency-Key': idempotencyKey },
    }),
  track: (orderNumber, email) => api.post('/orders/track', { orderNumber, email }),
};

export const paymentsAPI = {
  getCapabilities: () => api.get('/payments/capabilities'),
};

export const legalAPI = {
  getCheckoutDocuments: () => api.get('/legal/checkout-documents'),
};

export const b2bAPI = {
  getApplication: () => api.get('/b2b/application'),
  submitApplication: (data, idempotencyKey) => api.post('/b2b/applications', data, {
    headers: { 'Idempotency-Key': idempotencyKey },
  }),
  getQuotes: () => api.get('/b2b/quotes'),
  getQuote: (id) => api.get(`/b2b/quotes/${id}`),
  submitQuote: (data, idempotencyKey) => api.post('/b2b/quotes', data, {
    headers: { 'Idempotency-Key': idempotencyKey },
  }),
  acceptQuote: (id) => api.post(`/b2b/quotes/${id}/accept`),
};

export const fitmentAPI = {
  getMakes: () => api.get('/fitment/vehicles/makes'),
  getModels: (makeId) => api.get('/fitment/vehicles/models', { params: { makeId } }),
  getGenerations: (modelId) => api.get('/fitment/vehicles/generations', { params: { modelId } }),
  getEngines: (generationId) => api.get('/fitment/vehicles/engines', { params: { generationId } }),
  getConfigurations: (engineId) => api.get('/fitment/vehicles/configurations', { params: { engineId } }),
  check: (productId, vehicleId) => api.get('/fitment/check', { params: { productId, vehicleId } }),
  getForProduct: (productId, params) => api.get(`/fitment/products/${productId}`, { params }),
  findByIdentifier: (value, kind) => api.get(`/fitment/identifiers/${encodeURIComponent(value)}`, {
    params: kind ? { kind } : undefined,
  }),
};

export const garageAPI = {
  getAll: () => api.get('/garage'),
  createVehicle: (data, idempotencyKey) => api.post('/garage', data, {
    headers: { 'Idempotency-Key': idempotencyKey },
  }),
  updateVehicle: (id, data) => api.put(`/garage/${id}`, data),
  getMaintenance: (id) => api.get(`/garage/${id}/maintenance`),
  addMaintenance: (id, data, idempotencyKey) => api.post(`/garage/${id}/maintenance`, data, {
    headers: { 'Idempotency-Key': idempotencyKey },
  }),
  getReminders: (id) => api.get(`/garage/${id}/reminders`),
  addReminder: (id, data, idempotencyKey) => api.post(`/garage/${id}/reminders`, data, {
    headers: { 'Idempotency-Key': idempotencyKey },
  }),
  completeReminder: (id, concurrencyToken) =>
    api.post(`/garage/reminders/${id}/complete`, { concurrencyToken }),
};

export const adminAPI = {
  // Products
  getAllProducts: () => api.get('/admin/products'),
  createProduct: (productData) => api.post('/admin/products', productData),
  updateProduct: (id, productData) => api.put(`/admin/products/${id}`, productData),
  deleteProduct: (id) => api.delete(`/admin/products/${id}`),

  // Orders
  getAllOrders: () => api.get('/admin/orders'),
  getAllPayments: () => api.get('/admin/payments'),
  updateOrderStatus: (id, status) => api.put(`/admin/orders/${id}/status`, { status }),
  markPaymentPaid: (id) => api.post(`/admin/payments/${id}/mark-paid`),

  // Fulfillment
  getShipments: () => api.get('/admin/shipments'),
  createShipment: (orderId, items, idempotencyKey) =>
    api.post(`/admin/orders/${orderId}/shipments`, { items }, {
      headers: { 'Idempotency-Key': idempotencyKey },
    }),
  transitionShipment: (id, action, data) =>
    api.post(`/admin/shipments/${id}/${action}`, data),

  // Returns
  getReturns: () => api.get('/admin/returns'),
  createReturn: (orderId, items, idempotencyKey) =>
    api.post(`/admin/orders/${orderId}/returns`, { items }, {
      headers: { 'Idempotency-Key': idempotencyKey },
    }),
  transitionReturn: (id, action) => api.post(`/admin/returns/${id}/${action}`),

  // Provider readiness; does not expose credentials.
  getIntegrationCapabilities: () => api.get('/admin/integrations/capabilities'),
  getLegalDocuments: () => api.get('/admin/legal-documents'),
  createLegalDocument: (data) => api.post('/admin/legal-documents', data),
  publishLegalDocument: (id, concurrencyToken) =>
    api.post(`/admin/legal-documents/${id}/publish`, { concurrencyToken }),
  retireLegalDocument: (id, concurrencyToken) =>
    api.post(`/admin/legal-documents/${id}/retire`, { concurrencyToken }),

  // Verified catalog and vehicle compatibility operations
  upsertVehicleTree: (vehicle) => api.post('/admin/fitment/vehicles', vehicle),
  upsertProductFitment: (fitment) => api.post('/admin/fitment/links', fitment),
  upsertProductIdentifier: (identifier) => api.post('/admin/fitment/identifiers', identifier),
  getFitmentQuality: () => api.get('/admin/fitment/quality'),
  getGarageSummary: () => api.get('/admin/garage/summary'),
  getUserGarage: (userId) => api.get(`/admin/garage/users/${userId}`),

  // Append-only administrative audit metadata
  getAuditEvents: (params) => api.get('/admin/audit', { params }),
  verifyAuditChain: () => api.get('/admin/audit/verify'),

  // B2B dealer, pricing, quote and supplier operations
  getDealerApplications: () => api.get('/admin/b2b/applications'),
  reviewDealerApplication: (id, decision, customerGroupId) =>
    api.put(`/admin/b2b/applications/${id}/review`, { decision, customerGroupId }),
  getB2bPricing: () => api.get('/admin/b2b/pricing'),
  createCustomerGroup: (data) => api.post('/admin/b2b/customer-groups', data),
  updateCustomerGroup: (id, data) => api.put(`/admin/b2b/customer-groups/${id}`, data),
  createPriceList: (data) => api.post('/admin/b2b/price-lists', data),
  updatePriceList: (id, data) => api.put(`/admin/b2b/price-lists/${id}`, data),
  createPriceRule: (data) => api.post('/admin/b2b/price-rules', data),
  updatePriceRule: (id, data) => api.put(`/admin/b2b/price-rules/${id}`, data),
  getBulkQuotes: () => api.get('/admin/b2b/quotes'),
  prepareBulkQuote: (id, data) => api.put(`/admin/b2b/quotes/${id}/quote`, data),
  getSuppliers: () => api.get('/admin/b2b/suppliers'),
  createSupplier: (data) => api.post('/admin/b2b/suppliers', data),
  updateSupplier: (id, data) => api.put(`/admin/b2b/suppliers/${id}`, data),
  registerSupplierOffer: (data) => api.post('/admin/b2b/supplier-offers', data),
  setSupplierOfferActive: (id, data) => api.put(`/admin/b2b/supplier-offers/${id}/active`, data),
  selectSupplierSource: (data) => api.post('/admin/b2b/sourcing/select', data),

  // Sales channels expose capability/status only; credentials stay server-side.
  getSalesChannels: () => api.get('/admin/channels'),
  updateSalesChannelState: (id, data) => api.put(`/admin/channels/${id}/state`, data),
  refreshChannelListing: (channelId, productId, data) =>
    api.post(`/admin/channels/${channelId}/listings/${productId}/refresh`, data),

  // Stats
  getStats: () => api.get('/admin/stats'),

  // Users
  getAllUsers: () => api.get('/admin/users'),
  updateUserRole: (id, role) => api.put(`/admin/users/${id}/role`, { role }),
};

export default api;
