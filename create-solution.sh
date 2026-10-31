#!/bin/sh
# Создаёт .sln и добавляет все проекты
set -e
dotnet new sln -n DocRouter --force
dotnet sln add src/DocRouter.Core src/DocRouter.Engine src/DocRouter.Steps src/DocRouter.Routes src/DocRouter.Infrastructure src/DocRouter.Host
