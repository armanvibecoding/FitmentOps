import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { Link } from 'react-router';
import axios from 'axios';
import './OrderHistoryPage.css';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5167/api';

function OrderHistoryPage() {
  const { user, token } = useAuth();
  const [orders, setOrders] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [selectedOrder, setSelectedOrder] = useState(null);

  const fetchOrders = useCallback(async () => {
    if (!token) {
      setLoading(false);
      return;
    }

    try {
      const response = await axios.get(`${API_BASE_URL}/Orders`, {
        headers: { Authorization: `Bearer ${token}` }
      });
      setOrders(response.data);
    } catch (error) {
      console.error('Error fetching orders:', error);
      setError('Siparişler yüklenirken bir hata oluştu.');
    } finally {
      setLoading(false);
    }
  }, [token]);

  useEffect(() => {
    fetchOrders();
  }, [fetchOrders]);

  const getStatusBadgeClass = (status) => {
    switch (status?.toLowerCase()) {
      case 'pending':
        return 'status-pending';
      case 'processing':
        return 'status-processing';
      case 'shipped':
        return 'status-shipped';
      case 'delivered':
        return 'status-delivered';
      case 'cancelled':
        return 'status-cancelled';
      default:
        return 'status-pending';
    }
  };

  const getStatusText = (status) => {
    switch (status?.toLowerCase()) {
      case 'pending':
        return 'Beklemede';
      case 'processing':
        return 'İşleniyor';
      case 'shipped':
        return 'Kargoda';
      case 'delivered':
        return 'Teslim Edildi';
      case 'cancelled':
        return 'İptal Edildi';
      default:
        return status || 'Beklemede';
    }
  };

  const formatDate = (dateString) => {
    const date = new Date(dateString);
    return date.toLocaleDateString('tr-TR', {
      year: 'numeric',
      month: 'long',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  };

  if (!user) {
    return (
      <div className="order-history-page">
        <div className="order-history-container">
          <div className="not-logged-in">
            <h2>Sipariş Geçmişi</h2>
            <p>Sipariş geçmişinizi görüntülemek için giriş yapmalısınız.</p>
            <Link to="/login" className="btn-login">Giriş Yap</Link>
          </div>
        </div>
      </div>
    );
  }

  if (loading) {
    return (
      <div className="order-history-page">
        <div className="order-history-container">
          <p className="loading">Siparişler yükleniyor...</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="order-history-page">
        <div className="order-history-container">
          <p className="error">{error}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="order-history-page">
      <div className="order-history-container">
        <h1>Sipariş Geçmişim</h1>

        {orders.length === 0 ? (
          <div className="no-orders">
            <p>Henüz siparişiniz bulunmamaktadır.</p>
            <Link to="/" className="btn-shop">Alışverişe Başla</Link>
          </div>
        ) : (
          <div className="orders-list">
            {orders.map((order) => (
              <div key={order.id} className="order-card">
                <div className="order-header">
                  <div className="order-info">
                    <h3>Sipariş #{order.orderNumber}</h3>
                    <p className="order-date">{formatDate(order.orderDate)}</p>
                  </div>
                  <div className="order-status">
                    <span className={`status-badge ${getStatusBadgeClass(order.status)}`}>
                      {getStatusText(order.status)}
                    </span>
                  </div>
                </div>

                <div className="order-body">
                  <div className="order-items">
                    <h4>Ürünler:</h4>
                    <ul>
                      {order.orderItems?.map((item, index) => (
                        <li key={index}>
                          <span className="item-name">
                            {item.product?.name || 'Ürün'}
                          </span>
                          <span className="item-quantity">x{item.quantity}</span>
                          <span className="item-price">{item.price?.toFixed(2)} TL</span>
                        </li>
                      ))}
                    </ul>
                  </div>

                  <div className="order-delivery">
                    <h4>Teslimat Adresi:</h4>
                    <p>{order.shippingAddress}</p>
                    <p>{order.city} / {order.postalCode}</p>
                  </div>
                </div>

                <div className="order-footer">
                  <div className="order-total">
                    <span className="total-label">Toplam:</span>
                    <span className="total-amount">{order.totalAmount?.toFixed(2)} TL</span>
                  </div>
                  <button
                    className="btn-details"
                    onClick={() => setSelectedOrder(selectedOrder?.id === order.id ? null : order)}
                  >
                    {selectedOrder?.id === order.id ? 'Detayları Gizle' : 'Detayları Göster'}
                  </button>
                </div>

                {selectedOrder?.id === order.id && (
                  <div className="order-details">
                    <div className="details-section">
                      <h4>Müşteri Bilgileri:</h4>
                      <p><strong>Ad Soyad:</strong> {order.customerName}</p>
                      <p><strong>E-posta:</strong> {order.customerEmail}</p>
                      <p><strong>Telefon:</strong> {order.customerPhone}</p>
                    </div>
                    <div className="details-section">
                      <h4>Sipariş Detayları:</h4>
                      <p><strong>Sipariş No:</strong> {order.orderNumber}</p>
                      <p><strong>Sipariş Tarihi:</strong> {formatDate(order.orderDate)}</p>
                      <p><strong>Durum:</strong> {getStatusText(order.status)}</p>
                    </div>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

export default OrderHistoryPage;
