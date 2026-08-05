import { useState, useEffect } from 'react';
import { useSearchParams } from 'react-router';
import ProductCard from '../components/ProductCard';
import { productsAPI } from '../services/api';

const SearchPage = () => {
  const [searchParams] = useSearchParams();
  const [products, setProducts] = useState([]);
  const [loading, setLoading] = useState(true);
  const query = searchParams.get('q') || '';

  useEffect(() => {
    const searchProducts = async () => {
      if (!query) {
        setProducts([]);
        setLoading(false);
        return;
      }

      try {
        setLoading(true);
        const response = await productsAPI.search(query);
        setProducts(response.data);
      } catch (error) {
        console.error('Error searching products:', error);
        setProducts([]);
      } finally {
        setLoading(false);
      }
    };

    searchProducts();
  }, [query]);

  if (loading) {
    return (
      <div className="container">
        <div className="loading" style={{ textAlign: 'center', padding: '50px' }}>
          Aranıyor...
        </div>
      </div>
    );
  }

  return (
    <div className="container">
      <div style={{ marginTop: '30px', marginBottom: '30px' }}>
        <h1 className="section-title">
          Arama Sonuçları: "{query}"
        </h1>
        {products.length > 0 ? (
          <>
            <p style={{ textAlign: 'center', color: '#666', marginBottom: '30px' }}>
              {products.length} ürün bulundu
            </p>
            <div className="products-grid">
              {products.map((product) => (
                <ProductCard key={product.id} product={product} />
              ))}
            </div>
          </>
        ) : (
          <div style={{ textAlign: 'center', padding: '50px' }}>
            <p style={{ fontSize: '18px', color: '#666' }}>
              "{query}" için sonuç bulunamadı.
            </p>
            <p style={{ marginTop: '20px', color: '#999' }}>
              Farklı anahtar kelimeler deneyebilir veya kategorilerimize göz atabilirsiniz.
            </p>
          </div>
        )}
      </div>
    </div>
  );
};

export default SearchPage;
