FROM archont94/counter-strike1.6@sha256:2294ee98981ef1fe4443d9c6a802c74fc9639164ed7a22e294256afb46c32bdb AS game
FROM python@sha256:b18b261e1b4974b4f321451ad5251ded85abb22c2b8b262da527dda1d031ab30

COPY --from=game /hlds/ /opt/cs16/serverfiles-base/
COPY --from=game /lib32/ /lib32/
COPY --from=game /usr/lib32/ /usr/lib32/
RUN --mount=type=bind,source=lib32stdc++.deb,target=/tmp/lib32stdc++.deb,readonly \
    echo 'df69b110822b39f39600cd6ee889f4d7736b2105a7d9c8a100ce5f98d978abf0  /tmp/lib32stdc++.deb' | sha256sum -c - \
    && dpkg-deb -x /tmp/lib32stdc++.deb / \
    && ln -s /lib32/ld-linux.so.2 /lib/ld-linux.so.2 \
    && printf '/lib32\n/usr/lib32\n' > /etc/ld.so.conf.d/game-i386.conf \
    && ldconfig \
    && useradd --no-log-init --uid 1000 --user-group --home-dir /server --shell /usr/sbin/nologin steam \
    && /lib32/ld-linux.so.2 --list /opt/cs16/serverfiles-base/hlds_linux \
    && python3 --version

WORKDIR /
