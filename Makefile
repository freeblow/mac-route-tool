.PHONY: all build release universal app install uninstall run clean test icon

CONFIG ?= release
# 目标架构，空格分隔
export ARCHS ?= arm64 x86_64

all: build

build:
	swift build -c $(CONFIG)

release:
	swift build -c release

# 通用二进制：arm64 + x86_64
universal:
	bash scripts/build-universal.sh SRMGUI $(ARCHS)
	bash scripts/build-universal.sh srmd $(ARCHS)
	bash scripts/build-universal.sh srm $(ARCHS)

app:
	CONFIG=$(CONFIG) scripts/build-app.sh

install:
	sudo scripts/install.sh

uninstall:
	sudo scripts/uninstall.sh

run: app
	open dist/SRM.app

test:
	swift test

icon:
	swift scripts/generate-icon.swift

clean:
	rm -rf .build dist
