#!/bin/bash
# Installs the public address https://connection-manager.lloydstechnical.com.
# Run with:  sudo bash code/deploy/install-nginx-site.sh   (run.sh does this the first time)
# nginx forwards the address to the app on 127.0.0.1:5100; certbot adds the HTTPS certificate.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SITE=connection-manager.lloydstechnical.com

cp "$HERE/$SITE.nginx" /etc/nginx/sites-available/$SITE
ln -sf /etc/nginx/sites-available/$SITE /etc/nginx/sites-enabled/$SITE
nginx -t
systemctl reload nginx
certbot --nginx -d $SITE --non-interactive --redirect || certbot --nginx -d $SITE
echo "Done: https://$SITE/"
