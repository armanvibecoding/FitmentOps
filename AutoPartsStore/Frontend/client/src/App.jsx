import { BrowserRouter as Router, Routes, Route } from 'react-router';
import { CartProvider } from './context/CartContext';
import { AuthProvider } from './context/AuthContext';
import { WishlistProvider } from './context/WishlistContext';
import { NotificationProvider } from './context/NotificationContext';
import Header from './components/Header';
import Footer from './components/Footer';
import NotificationContainer from './components/NotificationContainer';
import ErrorBoundary from './components/ErrorBoundary';
import HomePage from './pages/HomePage';
import CategoryPage from './pages/CategoryPage';
import BrandPage from './pages/BrandPage';
import ProductDetailPage from './pages/ProductDetailPage';
import CartPage from './pages/CartPage';
import CheckoutPage from './pages/CheckoutPage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import AdminPage from './pages/AdminPage';
import ProfilePage from './pages/ProfilePage';
import OrderHistoryPage from './pages/OrderHistoryPage';
import WishlistPage from './pages/WishlistPage';
import AboutPage from './pages/AboutPage';
import ContactPage from './pages/ContactPage';
import CareerPage from './pages/CareerPage';
import OrderTrackingPage from './pages/OrderTrackingPage';
import ReturnsPage from './pages/ReturnsPage';
import B2bPage from './pages/B2bPage';
import GaragePage from './pages/GaragePage';
import FAQPage from './pages/FAQPage';
import SearchPage from './pages/SearchPage';
import NotFoundPage from './pages/NotFoundPage';
import './App.css';
import './auth-admin-styles.css';

function App() {
  return (
    <ErrorBoundary>
      <AuthProvider>
        <NotificationProvider>
          <WishlistProvider>
            <CartProvider>
              <Router>
                <NotificationContainer />
                <div style={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
                  <Header />
                <main style={{ flex: 1 }}>
                <Routes>
                <Route path="/" element={<HomePage />} />
                <Route path="/search" element={<SearchPage />} />
                <Route path="/category/:slug" element={<CategoryPage />} />
                <Route path="/brand/:slug" element={<BrandPage />} />
                <Route path="/product/:id" element={<ProductDetailPage />} />
                <Route path="/cart" element={<CartPage />} />
                <Route path="/checkout" element={<CheckoutPage />} />
                <Route path="/login" element={<LoginPage />} />
                <Route path="/register" element={<RegisterPage />} />
                <Route path="/admin" element={<AdminPage />} />
                <Route path="/profile" element={<ProfilePage />} />
                <Route path="/orders" element={<OrderHistoryPage />} />
                <Route path="/wishlist" element={<WishlistPage />} />
                <Route path="/hakkimizda" element={<AboutPage />} />
                <Route path="/iletisim" element={<ContactPage />} />
                <Route path="/kariyer" element={<CareerPage />} />
                <Route path="/siparis-takibi" element={<OrderTrackingPage />} />
                <Route path="/iade-degisim" element={<ReturnsPage />} />
                <Route path="/kurumsal" element={<B2bPage />} />
                <Route path="/garajim" element={<GaragePage />} />
                <Route path="/sss" element={<FAQPage />} />
                <Route path="*" element={<NotFoundPage />} />
              </Routes>            </main>
            <Footer />
          </div>
        </Router>
      </CartProvider>
    </WishlistProvider>
    </NotificationProvider>
    </AuthProvider>
    </ErrorBoundary>
  );
}

export default App;
