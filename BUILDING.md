# Building

Git Clone, Open folder in VSCode, it'll ask to install dependencies, click yes, you'll need to install .NET SDK 7.0 as well then type into the terminal:

`dotnet watch`

Browse to [http://localhost:44490/]

While developing you use vite as your webserver and it proxies /api and /ws to the dotnet webserver.

On deployment it's the opposite, the dotnet server runs and serves up precompiled html and js files.

Join us on discord for development talk: [https://discord.gg/jbqmn7V6n6]