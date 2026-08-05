import { useWishlist } from '../context/WishlistContext';
import { useCart } from '../context/CartContext';
import { Link } from 'react-router';
import './WishlistPage.css';

function WishlistPage() {
  const { wishlist, removeFromWishlist, clearWishlist } = useWishlist();
  const { addToCart } = useCart();

  const handleAddToCart = (product) => {
    addToCart(product);
  };

  const handleMoveAllToCart = () => {
    wishlist.forEach(product => addToCart(product));
    clearWishlist();
  };

  if (wishlist.length === 0) {
    return (
      <div className="wishlist-page">
        <div className="wishlist-container">
          <h1>Favori Ürünlerim</h1>
          <div className="empty-wishlist">
            <div className="empty-icon">❤️</div>
            <p>Favori listeniz boş.</p>
            <Link to="/" className="btn-shop">Alışverişe Başla</Link>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="wishlist-page">
      <div className="wishlist-container">
        <div className="wishlist-header">
          <h1>Favori Ürünlerim</h1>
          <div className="wishlist-actions">
            <button onClick={handleMoveAllToCart} className="btn-move-all">
              Tümünü Sepete Ekle
            </button>
            <button onClick={clearWishlist} className="btn-clear">
              Listeyi Temizle
            </button>
          </div>
        </div>

        <div className="wishlist-grid">
          {wishlist.map((product) => (
            <div key={product.id} className="wishlist-card">
              <button
                className="btn-remove"
                onClick={() => removeFromWishlist(product.id)}
                title="Favorilerden Çıkar"
              >
                ❌
              </button>

              <Link to={`/product/${product.id}`} className="product-link">
                <div className="product-image">
                  <img
                    src={product.imageUrl || 'https://via.placeholder.com/200'}
                    alt={product.name}
                  />
                </div>
                <div className="product-info">
                  <h3>{product.name}</h3>
                  {product.partNumber && (
                    <p className="part-number">Parça No: {product.partNumber}</p>
                  )}
                  {product.brand && (
                    <p className="brand">{product.brand.name}</p>
                  )}
                </div>
              </Link>

              <div className="product-footer">
                <div className="price-stock">
                  <span className="price">{product.price?.toFixed(2)} TL</span>
                  {product.stock > 0 ? (
                    <span className="in-stock">Stokta Var</span>
                  ) : (
                    <span className="out-of-stock">Stokta Yok</span>
                  )}
                </div>
                <button
                  className="btn-add-cart"
                  onClick={() => handleAddToCart(product)}
                  disabled={product.stock === 0}
                >
                  {product.stock > 0 ? 'Sepete Ekle' : 'Stokta Yok'}
                </button>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

export default WishlistPage;
