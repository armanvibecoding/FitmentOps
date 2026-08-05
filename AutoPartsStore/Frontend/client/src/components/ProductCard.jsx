import { useNavigate } from 'react-router';
import { useCart } from '../context/CartContext';

const ProductCard = ({ product }) => {
  const navigate = useNavigate();
  const { addToCart } = useCart();

  const handleAddToCart = (e) => {
    e.stopPropagation();
    addToCart(product);
  };

  const handleProductClick = () => {
    navigate(`/product/${product.id}`);
  };

  const renderStars = (rating) => {
    const stars = [];
    const fullStars = Math.floor(rating);

    for (let i = 0; i < fullStars; i++) {
      stars.push('⭐');
    }

    return stars.join('');
  };

  return (
    <div className="product-card" onClick={handleProductClick} style={{ cursor: 'pointer' }}>
      <div className="product-image-container">
        <img
          src={product.imageUrl || 'https://via.placeholder.com/300x225'}
          alt={product.name}
          className="product-image"
        />
        {product.badgeText && (
          <span className="product-badge">{product.badgeText}</span>
        )}
      </div>

      <div className="product-info">
        <div className="product-brand">{product.partBrand?.name || product.partBrand}</div>
        <h3 className="product-name">{product.name}</h3>

        <div className="product-rating">
          <span className="stars">{renderStars(product.rating)}</span>
          <span className="review-count">({product.reviewCount})</span>
        </div>

        <div className="product-price-container">
          <span className="product-price">{product.price.toFixed(2)} TL</span>
          {product.oldPrice && (
            <span className="product-old-price">{product.oldPrice.toFixed(2)} TL</span>
          )}
        </div>

        <button className="add-to-cart-button" onClick={handleAddToCart}>
          Sepete Ekle
        </button>
      </div>
    </div>
  );
};

export default ProductCard;
