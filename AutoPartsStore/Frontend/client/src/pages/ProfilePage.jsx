import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';
import axios from 'axios';
import './ProfilePage.css';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5167/api';

function ProfilePage() {
  const { user, updateUser, token } = useAuth();
  const [isEditing, setIsEditing] = useState(false);
  const [formData, setFormData] = useState({
    name: '',
    email: '',
    phone: ''
  });
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState({ type: '', text: '' });
  const [stats, setStats] = useState({
    totalOrders: 0,
    pendingOrders: 0,
    completedOrders: 0,
    totalSpent: 0
  });

  const fetchOrderStats = useCallback(async () => {
    if (!token) return;

    try {
      const response = await axios.get(`${API_BASE_URL}/Orders`, {
        headers: { Authorization: `Bearer ${token}` }
      });
      const orders = response.data;

      const totalOrders = orders.length;
      const pendingOrders = orders.filter(o => o.status?.toLowerCase() === 'pending' || o.status?.toLowerCase() === 'processing').length;
      const completedOrders = orders.filter(o => o.status?.toLowerCase() === 'delivered').length;
      const totalSpent = orders.reduce((sum, order) => sum + (order.totalAmount || 0), 0);

      setStats({
        totalOrders,
        pendingOrders,
        completedOrders,
        totalSpent
      });
    } catch (error) {
      console.error('Error fetching order stats:', error);
    }
  }, [token]);

  useEffect(() => {
    if (user) {
      setFormData({
        name: user.fullName || '',
        email: user.email || '',
        phone: user.phone || ''
      });
      fetchOrderStats();
    }
  }, [user, fetchOrderStats]);

  const handleChange = (e) => {
    setFormData({
      ...formData,
      [e.target.name]: e.target.value
    });
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setLoading(true);
    setMessage({ type: '', text: '' });

    try {
      const token = localStorage.getItem('token');
      const response = await axios.put(
        `${API_BASE_URL}/Auth/update-profile`,
        formData,
        {
          headers: { Authorization: `Bearer ${token}` }
        }
      );

      if (response.data) {
        updateUser(response.data);
        setIsEditing(false);
        setMessage({ type: 'success', text: 'Profiliniz başarıyla güncellendi!' });
      }
    } catch (error) {
      setMessage({
        type: 'error',
        text: error.response?.data?.message || 'Profil güncellenirken bir hata oluştu.'
      });
    } finally {
      setLoading(false);
    }
  };

  if (!user) {
    return (
      <div className="profile-page">
        <div className="profile-container">
          <p>Profilinizi görüntülemek için giriş yapmalısınız.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="profile-page">
      <div className="profile-container">
        <h1>Profilim</h1>

        {message.text && (
          <div className={`message ${message.type}`}>
            {message.text}
          </div>
        )}

        <div className="profile-content">
          <div className="profile-info-section">
            <h2>Kullanıcı Bilgileri</h2>

            {!isEditing ? (
              <div className="profile-view">
                <div className="info-item">
                  <label>Ad Soyad:</label>
                  <span>{user.fullName}</span>
                </div>
                <div className="info-item">
                  <label>E-posta:</label>
                  <span>{user.email}</span>
                </div>
                <div className="info-item">
                  <label>Telefon:</label>
                  <span>{user.phone || 'Belirtilmemiş'}</span>
                </div>
                <div className="info-item">
                  <label>Rol:</label>
                  <span className="role-badge">{user.role}</span>
                </div>
                <button
                  className="btn-edit"
                  onClick={() => setIsEditing(true)}
                >
                  Düzenle
                </button>
              </div>
            ) : (
              <form className="profile-edit" onSubmit={handleSubmit}>
                <div className="form-group">
                  <label htmlFor="name">Ad Soyad:</label>
                  <input
                    type="text"
                    id="name"
                    name="name"
                    value={formData.name}
                    onChange={handleChange}
                    required
                  />
                </div>
                <div className="form-group">
                  <label htmlFor="email">E-posta:</label>
                  <input
                    type="email"
                    id="email"
                    name="email"
                    value={formData.email}
                    onChange={handleChange}
                    required
                  />
                </div>
                <div className="form-group">
                  <label htmlFor="phone">Telefon:</label>
                  <input
                    type="tel"
                    id="phone"
                    name="phone"
                    value={formData.phone}
                    onChange={handleChange}
                  />
                </div>
                <div className="form-actions">
                  <button
                    type="submit"
                    className="btn-save"
                    disabled={loading}
                  >
                    {loading ? 'Kaydediliyor...' : 'Kaydet'}
                  </button>
                  <button
                    type="button"
                    className="btn-cancel"
                    onClick={() => {
                      setIsEditing(false);
                      setFormData({
                        name: user.fullName || '',
                        email: user.email || '',
                        phone: user.phone || ''
                      });
                    }}
                    disabled={loading}
                  >
                    İptal
                  </button>
                </div>
              </form>
            )}
          </div>

          <div className="profile-stats-section">
            <h2>Hesap İstatistikleri</h2>
            <div className="stats-grid">
              <div className="stat-card">
                <div className="stat-icon">📦</div>
                <div className="stat-info">
                  <span className="stat-label">Toplam Sipariş</span>
                  <span className="stat-value">{stats.totalOrders}</span>
                </div>
              </div>
              <div className="stat-card">
                <div className="stat-icon">⏳</div>
                <div className="stat-info">
                  <span className="stat-label">Bekleyen Sipariş</span>
                  <span className="stat-value">{stats.pendingOrders}</span>
                </div>
              </div>
              <div className="stat-card">
                <div className="stat-icon">✅</div>
                <div className="stat-info">
                  <span className="stat-label">Tamamlanan</span>
                  <span className="stat-value">{stats.completedOrders}</span>
                </div>
              </div>
              <div className="stat-card">
                <div className="stat-icon">💰</div>
                <div className="stat-info">
                  <span className="stat-label">Toplam Harcama</span>
                  <span className="stat-value">{stats.totalSpent.toFixed(2)} TL</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export default ProfilePage;
