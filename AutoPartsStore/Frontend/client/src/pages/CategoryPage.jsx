import { useState, useEffect } from 'react';
import { useParams } from 'react-router';
import ProductCard from '../components/ProductCard';
import { categoriesAPI, productsAPI } from '../services/api';

const CategoryPage = () => {
  const { slug } = useParams();
  const [category, setCategory] = useState(null);
  const [products, setProducts] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);
        const categoryResponse = await categoriesAPI.getBySlug(slug);
        setCategory(categoryResponse.data);

        const productsResponse = await productsAPI.getAll({
          categoryId: categoryResponse.data.id,
        });
        setProducts(productsResponse.data);
      } catch (error) {
        console.error('Error fetching category data:', error);
      } finally {
        setLoading(false);
      }
    };

    fetchData();
  }, [slug]);

  if (loading) {
    return <div className="loading">Yükleniyor...</div>;
  }

  if (!category) {
    return (
      <div className="container">
        <h2>Kategori bulunamadı</h2>
      </div>
    );
  }

  return (
    <div className="container" style={{ paddingTop: '40px', paddingBottom: '40px' }}>
      <h1 className="section-title">{category.name}</h1>
      <p style={{ textAlign: 'center', marginBottom: '40px', color: '#666' }}>
        {category.description}
      </p>

      {products.length === 0 ? (
        <p style={{ textAlign: 'center' }}>Bu kategoride henüz ürün bulunmamaktadır.</p>
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

export default CategoryPage;
