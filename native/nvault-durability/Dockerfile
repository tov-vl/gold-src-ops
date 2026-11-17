FROM archont94/counter-strike1.6@sha256:2294ee98981ef1fe4443d9c6a802c74fc9639164ed7a22e294256afb46c32bdb
USER root
RUN printf 'deb https://archive.debian.org/debian buster main\n' > /etc/apt/sources.list && rm -f /etc/apt/sources.list.d/* && apt-get -o Acquire::Check-Valid-Until=false update && apt-get -y --no-install-recommends install g++-multilib=4:8.3.0-1 binutils=2.31.1-16 make=4.2.1-1.2 python3=3.7.3-1 && rm -rf /var/lib/apt/lists/*
ENTRYPOINT []
