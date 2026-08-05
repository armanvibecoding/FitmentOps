const optionalSetting = (value) => (typeof value === 'string' ? value.trim() : '');

export const brand = Object.freeze({
  name: 'FitmentOps',
  tagline: 'Automotive Commerce & Operations Platform',
  supportEmail: optionalSetting(import.meta.env.VITE_SUPPORT_EMAIL),
  supportPhone: optionalSetting(import.meta.env.VITE_SUPPORT_PHONE),
  businessAddress: optionalSetting(import.meta.env.VITE_BUSINESS_ADDRESS),
  careerEmail: optionalSetting(import.meta.env.VITE_CAREER_EMAIL),
});
