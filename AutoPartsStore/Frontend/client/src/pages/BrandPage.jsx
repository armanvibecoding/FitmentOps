import { useState, useEffect } from 'react';
import { useParams } from 'react-router';
import ProductCard from '../components/ProductCard';
import { productsAPI, brandsAPI } from '../services/api';

const BrandPage = () => {
  const { slug } = useParams();
  const [products, setProducts] = useState([]);
  const [brand, setBrand] = useState(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);

        // Önce brand bilgisini al
        const brandResponse = await brandsAPI.getBySlug(slug);
        setBrand(brandResponse.data);

        // Sonra bu brand'e ait ürünleri al
        const productsResponse = await productsAPI.getAll({ brandId: brandResponse.data.id });
        setProducts(productsResponse.data);
      } catch (error) {
        console.error('Error fetching data:', error);
      } finally {
        setLoading(false);
      }
    };

    fetchData();
  }, [slug]);

  if (loading) {
    return <div className="loading">Yükleniyor...</div>;
  }

  if (!brand) {
    return <div className="container"><p>Marka bulunamadı.</p></div>;
  }

  return (
    <div className="container">
      <div className="category-header">
        <h1>{brand.name} Ürünleri</h1>
        <p>{products.length} ürün bulundu</p>
      </div>

      {products.length === 0 ? (
        <p className="no-products">Bu marka için henüz ürün bulunmamaktadır.</p>
      ) : (
        <div className="products-grid">
          {products.map((product) => (
            <ProductCard key={product.id} product={product} />
          ))}
        </div>
      )}
    </div>
  );
};

export default BrandPage;
