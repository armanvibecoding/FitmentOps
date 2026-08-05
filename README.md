<div align="center">

# FitmentOps

### Fitment-aware Automotive Commerce & Operations Platform

Araç uyumluluğu odaklı otomotiv ticaret ve operasyon platformu

[![CI](https://github.com/armanvibecoding/FitmentOps/actions/workflows/ci.yml/badge.svg)](https://github.com/armanvibecoding/FitmentOps/actions/workflows/ci.yml)
[![CodeQL](https://github.com/armanvibecoding/FitmentOps/actions/workflows/codeql.yml/badge.svg)](https://github.com/armanvibecoding/FitmentOps/actions/workflows/codeql.yml)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![React 19](https://img.shields.io/badge/React-19-61DAFB)](https://react.dev/)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

## Documentation / Dokümantasyon

### [English](README.en.md) · [Türkçe](README.tr.md)

</div>

---

FitmentOps unifies vehicle-fitment evidence, product discovery, checkout,
payments, refunds, fulfillment, returns, B2B pricing, supplier sourcing, and
administrative operations for the automotive aftermarket.

FitmentOps; araç uyumluluğu kanıtını, ürün keşfini, checkout, ödeme, iade,
sevkiyat, RMA, B2B fiyatlandırma, tedarikçi ve yönetim operasyonlarıyla tek
platformda birleştirir.

## Core engineering guarantees / Temel mühendislik güvenceleri

- Missing fitment evidence never becomes a positive compatibility claim.
- Price, stock, ownership, and payment state remain server-authoritative.
- Checkout, callbacks, refunds, and provider events are idempotent.
- External providers fail closed when they are unavailable or unconfigured.
- Critical administrative actions are policy-gated and auditable.

- Eksik uyumluluk kanıtı olumlu uyumluluk iddiasına dönüşmez.
- Fiyat, stok, sahiplik ve ödeme durumu için sunucu otoritedir.
- Checkout, callback, iade ve sağlayıcı olayları idempotent çalışır.
- Yapılandırılmamış harici sağlayıcılar güvenli biçimde kapalı kalır.
- Kritik yönetim işlemleri politika kontrollü ve denetlenebilirdir.

> [!IMPORTANT]
> FitmentOps is an engineering preview. Real payment, electronic-document,
> shipping, and marketplace adapters require provider implementation, legal
> review, sandbox certification, and staging evidence before production use.
>
> FitmentOps üretim öncesi mühendislik aşamasındadır. Gerçek ödeme, e-belge,
> kargo ve pazaryeri adaptörleri canlı kullanım öncesinde sağlayıcı
> implementasyonu, hukuki kontrol, sandbox sertifikasyonu ve staging kanıtı
> gerektirir.

## Repository guides / Repository rehberleri

- [Security policy / Güvenlik politikası](SECURITY.md)
- [Contribution guide / Katkı rehberi](CONTRIBUTING.md)
- [Apache License 2.0](LICENSE)
