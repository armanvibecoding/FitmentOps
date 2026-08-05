import { useState } from 'react';
import './InfoPages.css';

const FAQPage = () => {
  const [activeCategory, setActiveCategory] = useState('genel');

  const faqCategories = {
    genel: {
      title: 'Genel Sorular',
      questions: [
        {
          q: 'Parça Mühendisi nedir?',
          a: 'Parça Mühendisi, otomotiv yedek parça sektöründe 15 yıldır hizmet veren, geniş ürün yelpazesi ve kaliteli hizmet anlayışıyla Türkiye\'nin önde gelen e-ticaret platformlarından biridir.'
        },
        {
          q: 'Ürünleriniz orijinal mi?',
          a: 'Ürün portföyümüzde hem orijinal hem de yüksek kaliteli muadil parçalar bulunmaktadır. Her ürünün açıklamasında parça tipi belirtilmiştir.'
        },
        {
          q: 'Hangi markalara parça tedarik ediyorsunuz?',
          a: 'Tüm yerli ve yabancı araç markalarına uygun yedek parça çeşitlerimiz bulunmaktadır. Bosch, Mann Filter, Sachs, Brembo gibi dünyaca ünlü markalarla çalışmaktayız.'
        },
        {
          q: 'Fiziksel mağazanız var mı?',
          a: 'Evet, İstanbul Kadıköy\'de showroom mağazamız bulunmaktadır. Çalışma saatlerimiz: Hafta içi 09:00-18:00, Cumartesi 09:00-15:00'
        }
      ]
    },
    siparis: {
      title: 'Sipariş ve Ödeme',
      questions: [
        {
          q: 'Nasıl sipariş verebilirim?',
          a: 'Sitemizde beğendiğiniz ürünleri sepete ekleyerek, sepet sayfasından "Siparişi Tamamla" butonuna tıklayarak sipariş verebilirsiniz. Üye olmadan da misafir olarak alışveriş yapabilirsiniz.'
        },
        {
          q: 'Hangi ödeme yöntemlerini kabul ediyorsunuz?',
          a: 'Kredi kartı (Visa, MasterCard, Troy), banka havalesi/EFT ve kapıda ödeme seçenekleri ile ödeme yapabilirsiniz. Tüm kredi kartlarına taksit imkanı sunulmaktadır.'
        },
        {
          q: 'Taksit imkanı var mı?',
          a: 'Evet, tüm kredi kartlarına 9 taksit imkanı sunuyoruz. Bazı bankalarla özel kampanyalar sayesinde 12 taksit seçeneği de mevcuttur.'
        },
        {
          q: 'Siparişimi iptal edebilir miyim?',
          a: 'Siparişiniz henüz kargoya verilmemişse iptal edebilirsiniz. Müşteri hizmetlerimizi arayarak veya e-posta göndererek iptal talebinizi iletebilirsiniz.'
        },
        {
          q: 'Fatura kesiliyor mu?',
          a: 'Otomatik e-Fatura/e-Arşiv entegrasyonu henüz aktif değildir. Faturalama özelliği devreye alındığında sipariş ekranında açıkça gösterilecektir.'
        }
      ]
    },
    kargo: {
      title: 'Kargo ve Teslimat',
      questions: [
        {
          q: 'Kargo ücreti ne kadar?',
          a: '1000 TL ve üzeri alışverişlerde kargo ücretsizdir. 1000 TL altı siparişlerde kargo ücreti 49,90 TL\'dir.'
        },
        {
          q: 'Siparişim ne zaman kargoya verilir?',
          a: 'Siparişler stoklu ürünler için aynı gün veya en geç 1 iş günü içinde kargoya verilir. Stokta olmayan ürünler için müşteri hizmetlerimiz sizi bilgilendirir.'
        },
        {
          q: 'Kargo ile teslimat ne kadar sürer?',
          a: 'Kargoya verilen siparişler 2-4 iş günü içinde adresinize teslim edilir. Uzak bölgelerde teslimat süresi 3-5 iş günü olabilir.'
        },
        {
          q: 'Hangi kargo firmaları ile çalışıyorsunuz?',
          a: 'MNG Kargo, Yurtiçi Kargo ve PTT Kargo ile çalışmaktayız. Kargo firması sipariş anında otomatik olarak belirlenir.'
        },
        {
          q: 'Kargo takip numaramı nasıl öğrenirim?',
          a: 'Siparişiniz kargoya verildikten sonra, kargo takip numaranız e-posta ve SMS ile tarafınıza iletilir.'
        },
        {
          q: 'Yurtdışına kargo yapıyor musunuz?',
          a: 'Şu an için sadece Türkiye içine kargo gönderimi yapmaktayız. Yurtdışı gönderim için müşteri hizmetlerimizle iletişime geçebilirsiniz.'
        }
      ]
    },
    iade: {
      title: 'İade ve Değişim',
      questions: [
        {
          q: 'İade koşulları nelerdir?',
          a: 'Ürün teslim tarihinden itibaren 14 gün içinde, kullanılmamış, orijinal ambalajında ve faturası ile birlikte iade edebilirsiniz.'
        },
        {
          q: 'Montajı yapılmış ürünü iade edebilir miyim?',
          a: 'Hayır, montajı yapılmış veya kullanılmış ürünler iade kabul edilmez. Ancak üretim hatası varsa garanti kapsamında değerlendirilir.'
        },
        {
          q: 'İade kargo ücreti kimin tarafından karşılanır?',
          a: 'Hatalı/hasarlı ürün iadelerinde kargo ücreti tarafımızca karşılanır. Cayma hakkı kullanımında ilk kargo ücreti müşteriye, iade kargo ücreti firmamıza aittir.'
        },
        {
          q: 'Para iadesi ne kadar sürede yapılır?',
          a: 'Ürün depomuzda kontrol edildikten sonra 3-5 iş günü içinde ödeme iadeniz gerçekleştirilir. Kredi kartı iadelerinde bankanızın işlem süresine bağlı olarak 1-2 ekstre dönemi sürebilir.'
        },
        {
          q: 'Ürün değişimi nasıl yapılır?',
          a: 'İade sürecinin aynısı geçerlidir. İade talebinizde "değişim" belirtmeniz yeterlidir. Değişmek istediğiniz ürün stoklarımızda varsa en kısa sürede gönderilir.'
        }
      ]
    },
    urun: {
      title: 'Ürün ve Stok',
      questions: [
        {
          q: 'Aradığım parçayı nasıl bulabilirim?',
          a: 'Arama çubuğundan ürün adı, parça numarası veya marka girişi yapabilirsiniz. Ayrıca kategorilerden gezinerek de ürünleri inceleyebilirsiniz.'
        },
        {
          q: 'Ürün stokta yok, ne yapmalıyım?',
          a: 'Stokta olmayan ürünler için müşteri hizmetlerimizle iletişime geçin. Ürünü tedarik edip size ulaştırabiliriz.'
        },
        {
          q: 'Ürün garantisi var mı?',
          a: 'Tüm ürünlerimiz üretici firmanın garantisi altındadır. Garanti süreleri ürüne göre değişiklik göstermektedir ve ürün açıklamasında belirtilmiştir.'
        },
        {
          q: 'Toptan satış yapıyor musunuz?',
          a: 'Evet, toptan satış için özel fiyatlarımız mevcuttur. Kurumsal müşteri hizmetlerimizden bilgi alabilirsiniz.'
        },
        {
          q: 'Ürünler aracıma uyumlu mu nasıl anlarım?',
          a: 'Her ürünün açıklamasında uyumlu araç modelleri belirtilmiştir. Emin olamıyorsanız, müşteri hizmetlerimizden destek alabilirsiniz.'
        }
      ]
    },
    guvenlik: {
      title: 'Güvenlik ve Gizlilik',
      questions: [
        {
          q: 'Ödeme güvenliği nasıl sağlanıyor?',
          a: 'Tüm ödeme işlemleri 256-bit SSL sertifikası ile şifrelenmektedir. Kredi kartı bilgileriniz kesinlikle saklanmaz ve üçüncü kişilerle paylaşılmaz.'
        },
        {
          q: 'Kişisel bilgilerim güvende mi?',
          a: 'Evet, tüm kişisel bilgileriniz KVKK (Kişisel Verilerin Korunması Kanunu) kapsamında korunmaktadır ve üçüncü kişilerle paylaşılmamaktadır.'
        },
        {
          q: 'Üyelik bilgilerimi nasıl güncellerim?',
          a: 'Hesabım sayfasından tüm bilgilerinizi güncelleyebilirsiniz. Şifre değiştirmek için "Şifremi Unuttum" seçeneğini kullanabilirsiniz.'
        }
      ]
    }
  };

  return (
    <div className="info-page">
      <div className="container">
        <h1>Sıkça Sorulan Sorular</h1>

        <div className="info-content">
          <section className="info-section">
            <p>
              Aklınıza takılan soruların cevaplarını burada bulabilirsiniz.
              Aradığınız soruyu bulamadıysanız, müşteri hizmetlerimizle
              iletişime geçmekten çekinmeyin.
            </p>
          </section>

          <div className="faq-container">
            <div className="faq-categories">
              {Object.keys(faqCategories).map(key => (
                <button
                  key={key}
                  className={`category-btn ${activeCategory === key ? 'active' : ''}`}
                  onClick={() => setActiveCategory(key)}
                >
                  {faqCategories[key].title}
                </button>
              ))}
            </div>

            <div className="faq-content">
              <h2>{faqCategories[activeCategory].title}</h2>
              {faqCategories[activeCategory].questions.map((item, index) => (
                <div key={index} className="faq-item">
                  <h3>{item.q}</h3>
                  <p>{item.a}</p>
                </div>
              ))}
            </div>
          </div>

          <section className="info-section">
            <h2>Hala Sorunuz mu Var?</h2>
            <p>
              Aradığınız cevabı bulamadıysanız, müşteri hizmetlerimiz size
              yardımcı olmaktan mutluluk duyar.
            </p>
            <div className="contact-options">
              <div className="contact-option">
                <strong>Telefon:</strong> 0850 123 45 67<br />
                <small>Hafta içi 09:00 - 18:00</small>
              </div>
              <div className="contact-option">
                <strong>E-posta:</strong> info@parcamuhendisi.com<br />
                <small>24 saat içinde yanıt</small>
              </div>
              <div className="contact-option">
                <strong>Canlı Destek:</strong> Sağ alt köşeden<br />
                <small>Hafta içi 09:00 - 18:00</small>
              </div>
            </div>
          </section>
        </div>
      </div>
    </div>
  );
};

export default FAQPage;
