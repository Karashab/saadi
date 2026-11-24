# نصب پروژه نور روی سرور داخلی

این بسته روی **Ubuntu/Debian** و **Windows Server** یکسان اجرا می‌شود. تنها پیش‌نیازها **Git** و **Docker** (روی ویندوز: Docker Desktop یا Docker Engine با Compose v2) هستند.

## ۱. دریافت کد

```bash
git clone --branch platform https://github.com/Karashab/saadi.git noor
cd noor/deploy
cp .env.example .env        # ویندوز: Copy-Item .env.example .env
```

همه مقدارهای `.env` را عوض کنید. `NOOR_HOST` آدرسی است که کاربران در مرورگر می‌زنند (مثلاً `noor.bank.local`) و باید در DNS داخلی به این سرور اشاره کند.

## ۲. گواهی HTTPS

ورود امن فقط روی HTTPS کار می‌کند.

- **گواهی سازمان (پیشنهادی):** فایل‌ها را در `deploy/certs/noor.crt` و `deploy/certs/noor.key` بگذارید و در `.env` بنویسید: `TLS_MODE=/certs/noor.crt /certs/noor.key`
- **بدون گواهی سازمان:** `TLS_MODE=internal`. در این حالت گواهی ریشه Caddy را از مسیر `/data/caddy/pki/authorities/local/root.crt` داخل کانتینر `proxy` بردارید و روی سیستم کاربران نصب کنید.

## ۳. اجرا

```bash
docker compose up -d --build
docker compose ps
```

سرویس `migrate` ساختار دیتابیس را می‌سازد و تمام می‌شود؛ بقیه سرویس‌ها باید `running` باشند. اولین راه‌اندازی آنتی‌ویروس چند دقیقه طول می‌کشد.

## ۴. ساخت اولین مدیر

1. وارد `https://NOOR_HOST/auth/admin` شوید (کاربر و رمز `KEYCLOAK_ADMIN_*` در `.env`)، realm را روی **noor** بگذارید و کاربر خود را بسازید.
2. یک بار با همان کاربر وارد `https://NOOR_HOST` شوید.
3. روی سرور اجرا کنید: `./grant-admin.sh نام-کاربری` (ویندوز: داخل Git Bash).

از این به بعد کاربران را در Keycloak بسازید؛ هر کس یک بار وارد شود، در صفحه «دسترسی‌ها» قابل افزودن است.

## ۵. آپدیت خودکار از GitHub

- **Linux (cron، هر ۱۵ دقیقه):** `*/15 * * * * /opt/noor/deploy/update.sh >> /var/log/noor-update.log 2>&1`
- **Windows (Task Scheduler):** برنامه `powershell.exe` با آرگومان `-NoProfile -ExecutionPolicy Bypass -File C:\noor\deploy\update.ps1` هر ۱۵ دقیقه.

اسکریپت فقط وقتی شاخه `platform` تغییر کرده باشد دوباره می‌سازد. ساختار دیتابیس هم در همان اجرا به‌روز می‌شود.

## ۶. بکاپ

سرویس `backup` هر ۲۴ ساعت از دیتابیس و فایل‌ها در `BACKUP_DIR` نسخه می‌گیرد و ۱۴ روز نگه می‌دارد. **این پوشه را روی دیسک یا سرور دیگری کپی کنید**؛ بکاپ روی همان سرور در برابر خرابی دیسک محافظت نمی‌کند. دیتابیس Keycloak جدا است و باید با ابزار بکاپ خود سرور نگهداری شود.

بازگردانی دیتابیس: `docker compose exec -T db pg_restore -U noor_owner -d noor --clean < backups/db-YYYYMMDD-HHMM.dump`
