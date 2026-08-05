import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router';
import { brandsAPI } from '../services/api';
import './VehicleSearchBar.css';

const VehicleSearchBar = () => {
  const [brands, setBrands] = useState([]);
  const [selectedBrand, setSelectedBrand] = useState('');
  const navigate = useNavigate();

  useEffect(() => {
    const fetchBrands = async () => {
      try {
        const response = await brandsAPI.getAll();
        setBrands(response.data);
      } catch (error) {
        console.error('Error fetching brands:', error);
      }
    };

    fetchBrands();
  }, []);

  const handleSearch = () => {
    if (selectedBrand) {
      // Brand slug kullanarak ürünlere git
      navigate(`/brand/${selectedBrand}`);
    }
  };

  return (
    <div className="vehicle-search-bar">
      <div className="search-title">Araçla Uyumlu Parça Ara</div>
      <div className="search-content">
        <div className="search-field">
          <label>Marka</label>
          <select
            value={selectedBrand}
            onChange={(e) => setSelectedBrand(e.target.value)}
            className="brand-select"
          >
            <option value="">Araç Markası Seçin</option>
            {brands.map((brand) => (
              <option key={brand.id} value={brand.slug}>
                {brand.name}
              </option>
            ))}
          </select>
        </div>
        <button
          onClick={handleSearch}
          className="search-button"
          disabled={!selectedBrand}
        >
          Ara
        </button>
      </div>
    </div>
  );
};

export default VehicleSearchBar;
