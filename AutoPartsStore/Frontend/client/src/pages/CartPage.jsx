import { useCart } from '../context/CartContext';
import { Link } from 'react-router';

const CartPage = () => {
  const { cart, removeFromCart, updateQuantity, getCartTotal } = useCart();

  if (cart.length === 0) {
    return (
      <div className="container">
        <div className="empty-cart">
          <div className="empty-cart-icon">🛒</div>
          <h2>Sepetiniz Boş</h2>
          <p>Alışverişe başlamak için ürünleri keşfedin!</p>
          <Link to="/" className="hero-button" style={{ marginTop: '20px' }}>
            Alışverişe Başla
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div className="container cart-page">
      <h1 className="section-title">Sepetim ({cart.length} Ürün)</h1>

      <div className="cart-items">
        {cart.map((item) => (
          <div key={item.id} className="cart-item">
            <img
              src={item.imageUrl || 'https://via.placeholder.com/120'}
              alt={item.name}
              className="cart-item-image"
            />

            <div className="cart-item-info">
              <h3 className="cart-item-name">{item.name}</h3>
              <div className="product-brand">{item.partBrand?.name || item.partBrand || 'Marka Bilgisi Yok'}</div>
              <div className="cart-item-price">{item.price.toFixed(2)} TL</div>

              <div className="cart-item-quantity">
                <button
                  className="quantity-button"
                  onClick={() => updateQuantity(item.id, item.quantity - 1)}
                >
                  -
                </button>
                <span>{item.quantity}</span>
                <button
                  className="quantity-button"
                  onClick={() => updateQuantity(item.id, item.quantity + 1)}
                >
                  +
                </button>

                <button
                  className="remove-button"
                  onClick={() => removeFromCart(item.id)}
                >
                  Kaldır
                </button>
              </div>
            </div>

            <div>
              <strong>{(item.price * item.quantity).toFixed(2)} TL</strong>
            </div>
          </div>
        ))}
      </div>

      <div className="cart-summary">
        <div className="cart-total">
          <span>Toplam:</span>
          <span>{getCartTotal().toFixed(2)} TL</span>
        </div>

        <Link to="/checkout">
          <button className="checkout-button">Sipariş Ver</button>
        </Link>
      </div>
    </div>
  );
};

export default CartPage;
