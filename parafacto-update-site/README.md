# PARAFacto update (site statique)

Page unique pour la mise à jour : instructions, rappel de fermer l’app, puis téléchargement de l’installateur après **OK**.

## Déploiement Netlify (`parafactoupdate.netlify.app`)

1. Dans Netlify : **Add new site** → **Import an existing project** → dépôt Git `PARAFacto`.
2. **Base directory** : `parafacto-update-site` (important).
3. **Build command** : laisser vide (aucune build npm).
4. **Publish directory** : `public` (déjà indiqué dans `netlify.toml` à la racine de ce dossier ; si l’UI Netlify le demande, utilisez `public`).
5. Nom du site : par ex. **parafactoupdate** → URL `https://parafactoupdate.netlify.app/`.

Le dépôt configure `downloadPageUrl` dans `app-version.json` vers cette URL. Le site principal (`subscription-site`) doit exposer `app-version.json` en CORS (déjà prévu dans `subscription-site/public/_headers`).
